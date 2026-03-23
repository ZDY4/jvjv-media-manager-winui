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

    public async Task<SettingsDialogResult?> ShowAsync()
    {
        var panel = new SettingsPanel(_viewModel);

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

        panel.RemoveWatchedFolderButton.Click += (_, _) => panel.RemoveSelectedWatchedFolder();
        panel.ClearWatchedFoldersButton.Click += (_, _) => panel.ClearWatchedFolders();
        panel.ProtectFolderButton.Click += (_, _) => panel.ProtectSelectedFolder();
        panel.UnprotectFolderButton.Click += (_, _) => panel.UnprotectSelectedFolder();
        panel.ClearCacheButton.Click += (_, _) => _viewModel.ClearThumbnailCache();
        panel.ResetLibraryButton.Click += async (_, _) =>
        {
            var confirmed = await _dialogService.ConfirmAsync("清理缓存并重置库", "这会清空媒体记录与标签，但保留播放列表名称。是否继续？", "继续");
            if (confirmed)
            {
                await _viewModel.ResetLibraryAsync(false);
            }
        };
        panel.ClearAllButton.Click += async (_, _) =>
        {
            var confirmed = await _dialogService.ConfirmAsync("清理全部应用数据", "这会清空媒体库、标签、播放列表和监控文件夹配置。是否继续？", "清空");
            if (confirmed)
            {
                await _viewModel.ResetLibraryAsync(true);
                panel.ClearWatchedFolders();
            }
        };

        var dialog = new ContentDialog
        {
            Title = "设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await _dialogService.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        if (panel.HasProtectedFoldersWithoutPassword())
        {
            await _dialogService.ShowInfoAsync("设置未保存", "存在受保护文件夹时必须设置全局密码。");
            return null;
        }

        return new SettingsDialogResult
        {
            ThemeMode = panel.SelectedThemeMode,
            PortableModeEnabled = panel.PortableModeEnabled,
            DataDirectory = panel.DataDirectory,
            GlobalPassword = panel.GlobalPassword,
            WatchedFolders = panel.GetWatchedFolders()
        };
    }
}
