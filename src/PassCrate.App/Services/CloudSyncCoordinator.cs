using PassCrate.Core.Interfaces;

namespace PassCrate.App.Services;

public sealed class CloudSyncCoordinator : ICloudSyncScheduler, IDisposable
{
    private static readonly TimeSpan MutationDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(15);
    private readonly ICloudSyncService _syncService;
    private readonly ISyncRepository _repository;
    private readonly IKeyManagementService _keyManagement;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private bool _isForeground = true;
    private int _transientFailureCount;

    public CloudSyncCoordinator(
        ICloudSyncService syncService,
        ISyncRepository repository,
        IKeyManagementService keyManagement)
    {
        _syncService = syncService;
        _repository = repository;
        _keyManagement = keyManagement;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        _ = RunPeriodicAsync(_lifetime.Token);
    }

    public void NotifyVaultChanged() => Schedule(MutationDebounce);
    public void NotifyVaultUnlocked() => Schedule(TimeSpan.Zero);

    public void NotifyVaultLocked()
    {
        _debounce?.Cancel();
        _syncService.CancelActiveSync();
    }

    public void NotifyForegrounded()
    {
        _isForeground = true;
        Schedule(TimeSpan.Zero);
    }

    public void NotifyBackgrounded()
    {
        _isForeground = false;
        _debounce?.Cancel();
        _syncService.CancelActiveSync();
    }

    public void Dispose()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs eventArgs)
    {
        if (eventArgs.NetworkAccess == NetworkAccess.Internet)
        {
            Schedule(TimeSpan.Zero);
        }
    }

    private void Schedule(TimeSpan delay)
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = RunScheduledAsync(delay, _debounce.Token);
    }

    private async Task RunScheduledAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            await SynchronizeIfEligibleAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PassCrate.Core.Security.CloudAuthorizationRequiredException)
        {
            // Automatic retries pause until the user completes reconnect.
        }
        catch (PassCrate.Core.Security.CloudQuotaException exception)
        {
            Schedule(exception.RetryAfter ?? TimeSpan.FromMinutes(15));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            Schedule(NextTransientDelay());
        }
        catch
        {
            // ICloudSyncService exposes retry/reconnect status to the UI.
        }
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PeriodicInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await SynchronizeIfEligibleAsync(cancellationToken);
                    _transientFailureCount = 0;
                }
                catch (PassCrate.Core.Security.CloudAuthorizationRequiredException)
                {
                }
                catch (PassCrate.Core.Security.CloudQuotaException exception)
                {
                    Schedule(exception.RetryAfter ?? TimeSpan.FromMinutes(15));
                }
                catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
                {
                    Schedule(NextTransientDelay());
                }
                catch
                {
                    // A failed iteration never terminates the foreground timer.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SynchronizeIfEligibleAsync(CancellationToken cancellationToken)
    {
        if (!_isForeground || !_keyManagement.IsUnlocked)
        {
            return;
        }

        var configuration = await _repository.GetConfigurationAsync(cancellationToken);
        if (configuration is { IsEnabled: true })
        {
            await _syncService.SynchronizeAsync(cancellationToken);
            _transientFailureCount = 0;
        }
    }

    private TimeSpan NextTransientDelay()
    {
        var delays = new[] { 1, 2, 5, 15 };
        var index = Math.Min(_transientFailureCount++, delays.Length - 1);
        return TimeSpan.FromMinutes(delays[index]);
    }
}
