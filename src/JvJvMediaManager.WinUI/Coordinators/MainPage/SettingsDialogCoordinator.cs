using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.Controls;
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
        panel.PortableModeCommitted += (_, _) => _viewModel.SetPortableMode(panel.PortableModeEnabled);
        panel.DataDirectoryCommitted += (_, _) => _viewModel.SetDataDir(panel.DataDirectory);
        panel.GlobalPasswordCommitted += (_, _) => PersistGlobalPassword(panel);
        panel.NumpadTagShortcutsCommitted += (_, _) => _viewModel.SetNumpadTagShortcuts(panel.GetNumpadTagShortcuts());
        panel.WatchedFoldersChanged += (_, _) => PersistWatchedFolders(panel);

        panel.ChooseDataDirButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.SetDataDirectory(folder);
                panel.CommitDataDirectory();
            }
        };

        panel.AddWatchedFolderButton.Click += async (_, _) =>
        {
            var window = App.MainWindow;
            if (window == null)
            {
                return;
            }

            var folder = await PickerHelpers.PickFolderAsync(window);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                panel.TryAddWatchedFolder(folder);
            }
        };

        panel.ClearWatchedFoldersButton.Click += async (_, _) =>
        {
            try
            {
                AppTraceLogger.Log("SettingsDialog", $"Clear watched folders button clicked. PendingConfirm={panel.IsClearWatchedFoldersConfirmationPending}, Count={panel.WatchedFolders.Count}.");
                if (!panel.IsClearWatchedFoldersConfirmationPending)
                {
                    panel.BeginClearWatchedFoldersConfirmation();
                    return;
                }

                panel.ClearWatchedFolders();
            }
            catch (Exception ex)
            {
                AppTraceLogger.Log("SettingsDialog", $"Clear watched folders action failed: {ex}");
                App.WriteExceptionLog("SettingsDialog ClearWatchedFolders", ex);
                throw;
            }
        };
        panel.ClearCacheButton.Click += (_, _) => _viewModel.ClearThumbnailCache();
        panel.ResetLibraryButton.Click += async (_, _) =>
        {
            await _viewModel.ResetLibraryAsync(false);
        };
        panel.ClearAllButton.Click += async (_, _) =>
        {
            await _viewModel.ResetLibraryAsync(true);
            panel.ClearWatchedFolders();
        };

        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            CloseButtonText = "关闭",
        };

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

    private void PersistWatchedFolders(SettingsPanel panel)
    {
        AppTraceLogger.Log("SettingsDialog", $"PersistWatchedFolders invoked. PanelCount={panel.WatchedFolders.Count}, PendingConfirm={panel.IsClearWatchedFoldersConfirmationPending}.");
        if (panel.HasProtectedFoldersWithoutPassword())
        {
            panel.SetValidationMessage("存在受保护文件夹时必须设置全局密码。");
            return;
        }

        panel.ClearValidationMessage();
        _viewModel.SetLockPassword(panel.GlobalPassword);
        _viewModel.UpdateWatchedFolders(panel.GetWatchedFolders());
    }
}
