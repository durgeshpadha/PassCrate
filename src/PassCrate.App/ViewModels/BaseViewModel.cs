using CommunityToolkit.Mvvm.ComponentModel;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public interface ISensitiveStateViewModel
{
    void ClearSensitiveState();
}

public abstract partial class BaseViewModel : ObservableObject
{
    private static readonly IUserErrorMessageMapper ErrorMapper = new UserErrorMessageMapper();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool isBusy;

    [ObservableProperty]
    private string errorMessage = string.Empty;

    public bool IsNotBusy => !IsBusy;

    protected async Task RunBusyAsync(Func<Task> operation, string fallbackMessage)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var mapped = ErrorMapper.ToUserMessage(exception);
            ErrorMessage = string.IsNullOrWhiteSpace(mapped) ? fallbackMessage : mapped;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
