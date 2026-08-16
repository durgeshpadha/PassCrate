using System.Reflection;

namespace PassCrate.App.Services;

public sealed record CloudOAuthOptions
{
    public required string GoogleClientId { get; init; }
    public required string GoogleRedirectUri { get; init; }
    public required string DropboxAppKey { get; init; }
    public required string DropboxRedirectUri { get; init; }

    public static CloudOAuthOptions FromAssembly()
    {
        var values = typeof(CloudOAuthOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? string.Empty, StringComparer.Ordinal);
        return new CloudOAuthOptions
        {
            GoogleClientId = values.GetValueOrDefault("PassCrate.GoogleClientId", string.Empty),
            GoogleRedirectUri = values.GetValueOrDefault("PassCrate.GoogleRedirectUri", "passcrate://oauth2redirect/google"),
            DropboxAppKey = values.GetValueOrDefault("PassCrate.DropboxAppKey", string.Empty),
            DropboxRedirectUri = values.GetValueOrDefault("PassCrate.DropboxRedirectUri", "passcrate://oauth2redirect/dropbox"),
        };
    }
}
