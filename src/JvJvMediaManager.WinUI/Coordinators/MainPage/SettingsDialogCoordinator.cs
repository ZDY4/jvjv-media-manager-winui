using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class SettingsDialogCoordinator
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly IContentDialogService _dialogService;

    public SettingsDialogCoordinator(LibraryShellViewModel viewModel, IContentDialogService dialogService)
    {
        _viewModel = viewModel;
        _dialogService = dialogService;
    }

    public async Task ShowAsync()
    {
        AppTraceLogger.Log("SettingsDialog", $"Opening settings dialog. WatchedFolders={_viewModel.WatchedFolders.Count}, HasPassword={_viewModel.HasLockPassword}.");
        var panel = new SettingsPanel(_viewModel);
        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            CloseButtonText = "关闭",
        };
        using var session = new SettingsPanelSession(this, panel, dialog);

        try
        {
            var result = await _dialogService.ShowAsync(dialog);
            AppTraceLogger.Log("SettingsDialog", $"Settings dialog closed with result {result}.");
        }
        catch (Exception ex)
        {
            AppTraceLogger.Log("SettingsDialog", $"Settings dialog failed: {ex}");
            App.WriteExceptionLog("SettingsDialog ShowAsync", ex);
            throw;
        }
    }

    private sealed class SettingsPanelSession : IDisposable
    {
        private readonly SettingsDialogCoordinator _owner;
        private readonly SettingsPanel _panel;
        private readonly ContentDialog _dialog;

        public SettingsPanelSession(SettingsDialogCoordinator owner, SettingsPanel panel, ContentDialog dialog)
        {
            _owner = owner;
            _panel = panel;
            _dialog = dialog;
            _panel.PortableModeCommitted += Panel_PortableModeCommitted;
            _panel.DataDirectoryCommitted += Panel_DataDirectoryCommitted;
            _panel.GlobalPasswordCommitted += Panel_GlobalPasswordCommitted;
            _panel.NumpadTagShortcutsCommitted += Panel_NumpadTagShortcutsCommitted;
            _panel.ShortcutTagEditorRequested += Panel_ShortcutTagEditorRequested;
            _panel.ShortcutTagEditor.BackRequested += ShortcutTagEditor_BackRequested;
            _panel.WatchedFoldersChanged += Panel_WatchedFoldersChanged;
            _panel.ChooseDataDirButton.Click += ChooseDataDirButton_Click;
            _panel.AddWatchedFolderButton.Click += AddWatchedFolderButton_Click;
            _panel.ClearWatchedFoldersButton.Click += ClearWatchedFoldersButton_Click;
            _panel.ClearCacheButton.Click += ClearCacheButton_Click;
            _panel.ResetLibraryButton.Click += ResetLibraryButton_Click;
            _panel.ClearAllButton.Click += ClearAllButton_Click;
            _dialog.Closing += Dialog_Closing;
        }

        public void Dispose()
        {
            _panel.PortableModeCommitted -= Panel_PortableModeCommitted;
            _panel.DataDirectoryCommitted -= Panel_DataDirectoryCommitted;
            _panel.GlobalPasswordCommitted -= Panel_GlobalPasswordCommitted;
            _panel.NumpadTagShortcutsCommitted -= Panel_NumpadTagShortcutsCommitted;
            _panel.ShortcutTagEditorRequested -= Panel_ShortcutTagEditorRequested;
            _panel.ShortcutTagEditor.BackRequested -= ShortcutTagEditor_BackRequested;
            _panel.WatchedFoldersChanged -= Panel_WatchedFoldersChanged;
            _panel.ChooseDataDirButton.Click -= ChooseDataDirButton_Click;
            _panel.AddWatchedFolderButton.Click -= AddWatchedFolderButton_Click;
            _panel.ClearWatchedFoldersButton.Click -= ClearWatchedFoldersButton_Click;
            _panel.ClearCacheButton.Click -= ClearCacheButton_Click;
            _panel.ResetLibraryButton.Click -= ResetLibraryButton_Click;
            _panel.ClearAllButton.Click -= ClearAllButton_Click;
            _dialog.Closing -= Dialog_Closing;
        }

        private void Panel_PortableModeCommitted(object? sender, EventArgs e)
        {
            _owner._viewModel.SetPortableMode(_panel.PortableModeEnabled);
        }

        private void Panel_DataDirectoryCommitted(object? sender, EventArgs e)
        {
            _owner._viewModel.SetDataDir(_panel.DataDirectory);
        }

        private void Panel_GlobalPasswordCommitted(object? sender, EventArgs e)
        {
            _owner.PersistGlobalPassword(_panel);
        }

        private void Panel_NumpadTagShortcutsCommitted(object? sender, EventArgs e)
        {
            _owner._viewModel.SetNumpadTagShortcuts(_panel.GetNumpadTagShortcuts());
        }

        private void Panel_ShortcutTagEditorRequested(object? sender, EventArgs e)
        {
            _dialog.Title = "快捷菜单";
            _dialog.Content = _panel.ShortcutTagEditor;
        }

        private void ShortcutTagEditor_BackRequested(object? sender, EventArgs e)
        {
            _dialog.Title = "设置";
            _dialog.Content = _panel;
        }

        private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            _owner._viewModel.SetNumpadTagShortcuts(_panel.GetNumpadTagShortcuts());
        }

        private async void Panel_WatchedFoldersChanged(object? sender, EventArgs e)
        {
            try
            {
                await _owner.PersistWatchedFoldersAsync(_panel);
            }
            catch (Exception ex)
            {
                AppTraceLogger.LogException("SettingsDialog", "PersistWatchedFolders failed.", ex);
                App.WriteExceptionLog("SettingsDialog PersistWatchedFolders", ex);
                _panel.SetValidationMessage("保存监控文件夹失败。");
            }
        }

        private async void ChooseDataDirButton_Click(object sender, RoutedEventArgs e)
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                _panel.SetDataDirectory(folder);
                _panel.CommitDataDirectory();
            }
        }

        private async void AddWatchedFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                _panel.TryAddWatchedFolder(folder);
            }
        }

        private void ClearWatchedFoldersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppTraceLogger.Log("SettingsDialog", $"Clear watched folders button clicked. PendingConfirm={_panel.IsClearWatchedFoldersConfirmationPending}, Count={_panel.WatchedFolders.Count}.");
                if (!_panel.IsClearWatchedFoldersConfirmationPending)
                {
                    _panel.BeginClearWatchedFoldersConfirmation();
                    return;
                }

                _panel.ClearWatchedFolders();
            }
            catch (Exception ex)
            {
                AppTraceLogger.Log("SettingsDialog", $"Clear watched folders action failed: {ex}");
                App.WriteExceptionLog("SettingsDialog ClearWatchedFolders", ex);
                throw;
            }
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            await _owner._viewModel.ClearInvalidThumbnailCacheAsync();
        }

        private async void ResetLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            await _owner._viewModel.ResetLibraryAsync(false);
        }

        private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            await _owner._viewModel.ResetLibraryAsync(true);
            _panel.ClearWatchedFolders();
        }
    }

    private void PersistGlobalPassword(SettingsPanel panel)
    {
        AppTraceLogger.Log("SettingsDialog", $"PersistGlobalPassword invoked. PasswordLength={panel.GlobalPassword.Length}, HasProtectedFoldersWithoutPassword={panel.HasProtectedFoldersWithoutPassword()}.");
        if (panel.HasProtectedFoldersWithoutPassword())
        {
            panel.SetValidationMessage("存在受保护文件夹时必须设置全局密码。");
            return;
        }

        panel.ClearValidationMessage();
        _viewModel.SetLockPassword(panel.GlobalPassword);
    }

    private async Task PersistWatchedFoldersAsync(SettingsPanel panel)
    {
        AppTraceLogger.Log("SettingsDialog", $"PersistWatchedFolders invoked. PanelCount={panel.WatchedFolders.Count}, PendingConfirm={panel.IsClearWatchedFoldersConfirmationPending}.");
        if (panel.HasProtectedFoldersWithoutPassword())
        {
            panel.SetValidationMessage("存在受保护文件夹时必须设置全局密码。");
            return;
        }

        panel.ClearValidationMessage();
        _viewModel.SetLockPassword(panel.GlobalPassword);
        await _viewModel.UpdateWatchedFoldersAndImportNewAsync(panel.GetWatchedFolders());
    }
}
