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

        panel.ClearWatchedFoldersButton.Click += (_, _) => panel.ClearWatchedFolders();
        panel.ProtectFolderButton.Click += (_, _) => panel.ProtectSelectedFolder();
        panel.UnprotectFolderButton.Click += (_, _) => panel.UnprotectSelectedFolder();
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

        await _dialogService.ShowAsync(dialog);
    }

    private void PersistGlobalPassword(SettingsPanel panel)
    {
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
