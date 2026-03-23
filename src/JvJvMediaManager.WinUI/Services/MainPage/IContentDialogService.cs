using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Services.MainPage;

public interface IContentDialogService
{
    void AttachHost(XamlRoot xamlRoot);

    Task<ContentDialogResult> ShowAsync(ContentDialog dialog);

    Task ShowInfoAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string primaryButtonText);

    Task<string?> ShowTextInputAsync(string title, string placeholder, string initialValue, string primaryButtonText);

    Task<string?> ShowPasswordInputAsync(string title, string placeholder);

    Task<string?> ShowPlaylistColorDialogAsync(string title, string? colorHex);
}
