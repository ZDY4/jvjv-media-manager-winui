using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.Services.MainPage;

public sealed class ContentDialogService : IContentDialogService
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly object _infoKeySync = new();
    private XamlRoot? _xamlRoot;
    private string? _activeInfoKey;

    public void AttachHost(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot;
    }

    public Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        return ShowExclusiveAsync(dialog);
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var infoKey = $"{title}\n{message}";
        lock (_infoKeySync)
        {
            if (string.Equals(_activeInfoKey, infoKey, StringComparison.Ordinal))
            {
                AppTraceLogger.LogSampled(
                    "ContentDialog",
                    "duplicate-info-suppressed",
                    $"Suppressed duplicate info dialog. Title='{title}'.",
                    TimeSpan.FromSeconds(2));
                return;
            }

            _activeInfoKey = infoKey;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "关闭"
        };

        try
        {
            await ShowExclusiveAsync(dialog);
        }
        finally
        {
            lock (_infoKeySync)
            {
                if (string.Equals(_activeInfoKey, infoKey, StringComparison.Ordinal))
                {
                    _activeInfoKey = null;
                }
            }
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<string?> ShowTextInputAsync(string title, string placeholder, string initialValue, string primaryButtonText)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            PlaceholderText = placeholder,
            MinWidth = 280,
            Style = Application.Current.Resources["GlassTextBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? textBox.Text.Trim() : null;
    }

    public async Task<string?> ShowPasswordInputAsync(string title, string placeholder)
    {
        var passwordBox = new PasswordBox
        {
            PlaceholderText = placeholder,
            MinWidth = 280,
            Style = Application.Current.Resources["GlassPasswordBoxStyle"] as Style
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = passwordBox,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowAsync(dialog);
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    public async Task<string?> ShowPlaylistColorDialogAsync(string title, string? colorHex)
    {
        var initialColor = PlaylistBackgroundBrushConverter.TryParseColor(colorHex, out var parsedColor)
            ? parsedColor
            : ThemeResourceHelper.GetColor("AccentColor", Microsoft.UI.ColorHelper.FromArgb(255, 159, 178, 204));

        var colorPicker = new ColorPicker
        {
            Color = initialColor,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsMoreButtonVisible = true,
            MinWidth = 320
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = colorPicker,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "清除颜色",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await ShowAsync(dialog);
        return result switch
        {
            ContentDialogResult.Primary => $"#{colorPicker.Color.R:X2}{colorPicker.Color.G:X2}{colorPicker.Color.B:X2}",
            ContentDialogResult.Secondary => null,
            _ => colorHex
        };
    }

    private async Task<ContentDialogResult> ShowExclusiveAsync(ContentDialog dialog)
    {
        await _dialogGate.WaitAsync();
        try
        {
            if (_xamlRoot != null)
            {
                dialog.XamlRoot = _xamlRoot;
            }

            dialog.RequestedTheme = ElementTheme.Dark;
            dialog.Background ??= Application.Current.Resources["SurfaceBrush"] as Brush;
            dialog.Foreground ??= Application.Current.Resources["TextBrush"] as Brush;
            dialog.BorderBrush ??= Application.Current.Resources["SurfaceStrokeBrush"] as Brush;
            dialog.BorderThickness = new Thickness(1);

            return await dialog.ShowAsync().AsTask();
        }
        finally
        {
            _dialogGate.Release();
        }
    }
}
