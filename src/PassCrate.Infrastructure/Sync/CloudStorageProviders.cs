using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Sync;

public sealed class CloudStorageProviderFactory(IEnumerable<ICloudStorageProvider> providers) : ICloudStorageProviderFactory
{
    private readonly IReadOnlyDictionary<CloudProviderKind, ICloudStorageProvider> _providers =
        providers.ToDictionary(provider => provider.Kind);

    public ICloudStorageProvider Get(CloudProviderKind provider) =>
        _providers.TryGetValue(provider, out var implementation)
            ? implementation
            : throw new NotSupportedException($"{provider} is not available on this device.");
}

public sealed class GoogleDriveCloudStorageProvider(
    HttpClient httpClient,
    ICloudAuthorizationService authorization) : ICloudStorageProvider
{
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;

    public async Task<IReadOnlyList<CloudFileInfo>> ListAsync(
        int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        var files = new List<CloudFileInfo>();
        string? pageToken = null;
        do
        {
            var url = $"{FilesEndpoint}?spaces=appDataFolder&pageSize=1000&fields=nextPageToken,files(id,name,size,modifiedTime)";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            }

            using var request = await CreateRequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("files", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? string.Empty;
                    if (!name.EndsWith(".pvs", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    files.Add(new CloudFileInfo(
                        item.GetProperty("id").GetString()!,
                        name,
                        item.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var bytes) ? bytes : 0,
                        item.TryGetProperty("modifiedTime", out var modified) && DateTimeOffset.TryParse(modified.GetString(), out var timestamp)
                            ? timestamp
                            : null));
                    if (files.Count > maximumFiles)
                    {
                        throw new CloudResourceLimitException("Cloud cleanup is required before PassCrate can sync.");
                    }
                }
            }

            pageToken = document.RootElement.TryGetProperty("nextPageToken", out var next)
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
        return files;
    }

    public async Task<Stream> DownloadAsync(
        string providerFileId,
        long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            $"{FilesEndpoint}/{Uri.EscapeDataString(providerFileId)}?alt=media",
            cancellationToken).ConfigureAwait(false);
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            response.Dispose();
            throw new CloudResourceLimitException("The cloud snapshot exceeds PassCrate's size limit.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new BoundedHttpResponseStream(stream, response, maximumBytes);
    }

    public async Task<CloudFileInfo> UploadImmutableAsync(
        string opaqueName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var existing = (await ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(file => file.OpaqueName == opaqueName);
        if (existing is not null)
        {
            return existing;
        }

        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"{UploadEndpoint}?uploadType=multipart&fields=id,name,size,modifiedTime",
            cancellationToken).ConfigureAwait(false);
        using var multipart = new MultipartContent("related");
        multipart.Add(new StringContent(
            JsonSerializer.Serialize(new { name = opaqueName, parents = new[] { "appDataFolder" } }),
            Encoding.UTF8,
            "application/json"));
        var bytes = new ByteArrayContent(content.ToArray());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(bytes);
        request.Content = multipart;
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return new CloudFileInfo(
            document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("name").GetString()!,
            document.RootElement.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var length) ? length : content.Length,
            document.RootElement.TryGetProperty("modifiedTime", out var modified) && DateTimeOffset.TryParse(modified.GetString(), out var timestamp)
                ? timestamp
                : DateTimeOffset.UtcNow);
    }

    public async Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Delete,
            $"{FilesEndpoint}/{Uri.EscapeDataString(providerFileId)}",
            cancellationToken).ConfigureAwait(false);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await authorization.GetAccessTokenAsync(Kind, cancellationToken).ConfigureAwait(false));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is 429 or 507)
        {
            throw new CloudQuotaException(response.Headers.RetryAfter?.Delta);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CloudAuthorizationRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Google Drive request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }
}

public sealed class DropboxCloudStorageProvider(
    HttpClient httpClient,
    ICloudAuthorizationService authorization) : ICloudStorageProvider
{
    private const string ApiEndpoint = "https://api.dropboxapi.com/2/files";
    private const string ContentEndpoint = "https://content.dropboxapi.com/2/files";
    public CloudProviderKind Kind => CloudProviderKind.Dropbox;

    public async Task<IReadOnlyList<CloudFileInfo>> ListAsync(
        int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        var files = new List<CloudFileInfo>();
        string? cursor = null;
        do
        {
            var method = cursor is null ? "list_folder" : "list_folder/continue";
            var body = cursor is null
                ? JsonSerializer.Serialize(new { path = string.Empty, recursive = false, include_deleted = false })
                : JsonSerializer.Serialize(new { cursor });
            using var request = await CreateRequestAsync(HttpMethod.Post, $"{ApiEndpoint}/{method}", cancellationToken).ConfigureAwait(false);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            foreach (var item in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (item.GetProperty(".tag").GetString() != "file")
                {
                    continue;
                }

                var name = item.GetProperty("name").GetString() ?? string.Empty;
                if (!name.EndsWith(".pvs", StringComparison.Ordinal))
                {
                    continue;
                }

                files.Add(new CloudFileInfo(
                    item.GetProperty("path_lower").GetString()!,
                    name,
                    item.GetProperty("size").GetInt64(),
                    item.TryGetProperty("server_modified", out var modified) && DateTimeOffset.TryParse(modified.GetString(), out var timestamp)
                        ? timestamp
                        : null));
                if (files.Count > maximumFiles)
                {
                    throw new CloudResourceLimitException("Cloud cleanup is required before PassCrate can sync.");
                }
            }

            cursor = document.RootElement.GetProperty("has_more").GetBoolean()
                ? document.RootElement.GetProperty("cursor").GetString()
                : null;
        }
        while (cursor is not null);
        return files;
    }

    public async Task<Stream> DownloadAsync(
        string providerFileId,
        long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"{ContentEndpoint}/download", cancellationToken).ConfigureAwait(false);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = providerFileId }));
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
        {
            response.Dispose();
            throw new CloudResourceLimitException("The cloud snapshot exceeds PassCrate's size limit.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new BoundedHttpResponseStream(stream, response, maximumBytes);
    }

    public async Task<CloudFileInfo> UploadImmutableAsync(
        string opaqueName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var existing = (await ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(file => file.OpaqueName == opaqueName);
        if (existing is not null)
        {
            return existing;
        }

        using var request = await CreateRequestAsync(HttpMethod.Post, $"{ContentEndpoint}/upload", cancellationToken).ConfigureAwait(false);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
        {
            path = $"/{opaqueName}",
            mode = "add",
            autorename = false,
            mute = true,
            strict_conflict = false,
        }));
        request.Content = new ByteArrayContent(content.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return new CloudFileInfo(
            document.RootElement.GetProperty("path_lower").GetString()!,
            document.RootElement.GetProperty("name").GetString()!,
            document.RootElement.GetProperty("size").GetInt64(),
            document.RootElement.TryGetProperty("server_modified", out var modified) && DateTimeOffset.TryParse(modified.GetString(), out var timestamp)
                ? timestamp
                : DateTimeOffset.UtcNow);
    }

    public async Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"{ApiEndpoint}/delete_v2", cancellationToken).ConfigureAwait(false);
        request.Content = new StringContent(JsonSerializer.Serialize(new { path = providerFileId }), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await authorization.GetAccessTokenAsync(Kind, cancellationToken).ConfigureAwait(false));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is 429 or 507)
        {
            throw new CloudQuotaException(response.Headers.RetryAfter?.Delta);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new CloudAuthorizationRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Dropbox request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
    }
}

internal sealed class BoundedHttpResponseStream(
    Stream inner,
    HttpResponseMessage response,
    long maximumBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Count(int count)
    {
        _bytesRead = checked(_bytesRead + count);
        if (_bytesRead > maximumBytes)
        {
            throw new CloudResourceLimitException("The cloud snapshot exceeded PassCrate's size limit while downloading.");
        }
    }
}
