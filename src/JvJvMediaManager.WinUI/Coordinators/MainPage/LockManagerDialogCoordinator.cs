using System.Collections.ObjectModel;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services.MainPage;
using JvJvMediaManager.ViewModels.MainPage;
using JvJvMediaManager.Views.Controls;
using Microsoft.UI.Xaml.Controls;

namespace JvJvMediaManager.Coordinators.MainPage;

public sealed class LockManagerDialogCoordinator
{
    private readonly LibraryShellViewModel _viewModel;
    private readonly IContentDialogService _dialogService;

    public LockManagerDialogCoordinator(LibraryShellViewModel viewModel, IContentDialogService dialogService)
    {
        _viewModel = viewModel;
        _dialogService = dialogService;
    }

    public async Task<FolderLockResult?> ShowAsync()
    {
        var protectedFolders = new ObservableCollection<WatchedFolder>(_viewModel.GetProtectedFolders());
        if (protectedFolders.Count == 0)
        {
            await _dialogService.ShowInfoAsync("锁定管理", "当前没有受保护的监控文件夹。请先在设置中为文件夹启用保护。");
            return null;
        }

        var panel = new LockManagerPanel(protectedFolders);
        panel.FoldersListView.DisplayMemberPath = nameof(WatchedFolder.Path);

        void RefreshStatus()
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                panel.StatusText.Text = "请选择一个受保护文件夹。";
                return;
            }

            panel.StatusText.Text = _viewModel.IsFolderUnlocked(selected.Path)
                ? $"当前状态：已解锁。{Path.GetFileName(selected.Path)} 中的媒体现在可见。"
                : $"当前状态：已锁定。输入全局密码后可解锁 {Path.GetFileName(selected.Path)}。";
        }

        panel.FoldersListView.SelectionChanged += (_, _) => RefreshStatus();
        RefreshStatus();

        panel.UnlockButton.Click += async (_, _) =>
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                return;
            }

            var password = await _dialogService.ShowPasswordInputAsync("解锁文件夹", "输入全局密码");
            if (password == null)
            {
                return;
            }

            var unlocked = await _viewModel.UnlockFolderAsync(selected.Path, password);
            if (!unlocked)
            {
                panel.StatusText.Text = "密码错误，未能解锁文件夹。";
                return;
            }

            RefreshStatus();
        };

        panel.RelockButton.Click += async (_, _) =>
        {
            if (panel.SelectedFolder is not WatchedFolder selected)
            {
                return;
            }

            await _viewModel.LockFolderAsync(selected.Path);
            RefreshStatus();
        };

        panel.RelockAllButton.Click += async (_, _) =>
        {
            await _viewModel.RelockAllFoldersAsync();
            RefreshStatus();
        };

        var dialog = new ContentDialog
        {
            Title = "锁定管理",
            Content = panel,
            CloseButtonText = "关闭"
        };

        await _dialogService.ShowAsync(dialog);
        return new FolderLockResult { Changed = true };
    }
}
