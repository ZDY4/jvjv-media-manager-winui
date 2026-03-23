using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using JvJvMediaManager.Models;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class SettingsPanel : UserControl
{
    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public SettingsPanel(LibraryShellViewModel viewModel)
    {
        InitializeComponent();

        ThemeModeComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "跟随系统", Tag = AppThemeMode.System },
            new ComboBoxItem { Content = "浅色", Tag = AppThemeMode.Light },
            new ComboBoxItem { Content = "深色", Tag = AppThemeMode.Dark }
        };
        ThemeModeComboBox.SelectedIndex = viewModel.ThemeMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
        PortableToggle.IsOn = viewModel.PortableMode;
        DataDirTextBox.Text = viewModel.ConfiguredDataDir ?? viewModel.DataDir;
        GlobalPasswordBox.Password = viewModel.LockPassword;

        WatchedFolders = new ObservableCollection<WatchedFolder>(
            viewModel.WatchedFolders.Select(item => new WatchedFolder
            {
                Path = item.Path,
                Locked = item.Locked
            }));

        WatchedFoldersList.ItemsSource = WatchedFolders;
        WatchedFoldersList.SelectionChanged += WatchedFoldersList_SelectionChanged;

        if (WatchedFolders.Count > 0)
        {
            WatchedFoldersList.SelectedIndex = 0;
        }
        else
        {
            RefreshWatchedFolderStatus();
        }
    }

    public string DataDirectory => DataDirTextBox.Text.Trim();

    public string GlobalPassword => GlobalPasswordBox.Password;

    public bool PortableModeEnabled => PortableToggle.IsOn;

    public AppThemeMode SelectedThemeMode =>
        (AppThemeMode?)((ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Tag ?? AppThemeMode.System) ?? AppThemeMode.System;

    public WatchedFolder? SelectedWatchedFolder => WatchedFoldersList.SelectedItem as WatchedFolder;

    public void SetValidationMessage(string message)
    {
        ValidationText.Text = message;
    }

    public void ClearValidationMessage()
    {
        ValidationText.Text = string.Empty;
    }

    public void SetDataDirectory(string path)
    {
        DataDirTextBox.Text = path;
    }

    public bool TryAddWatchedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)
            || WatchedFolders.Any(item => string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var watchedFolder = new WatchedFolder { Path = folder, Locked = false };
        WatchedFolders.Add(watchedFolder);
        WatchedFoldersList.SelectedItem = watchedFolder;
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        return true;
    }

    public void RemoveSelectedWatchedFolder()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            return;
        }

        var nextIndex = Math.Max(0, WatchedFolders.IndexOf(folder) - 1);
        WatchedFolders.Remove(folder);
        WatchedFoldersList.SelectedIndex = WatchedFolders.Count == 0 ? -1 : Math.Min(nextIndex, WatchedFolders.Count - 1);
        RefreshWatchedFolderStatus();
    }

    public void ClearWatchedFolders()
    {
        WatchedFolders.Clear();
        WatchedFoldersList.SelectedIndex = -1;
        RefreshWatchedFolderStatus();
    }

    public bool ProtectSelectedFolder()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            SetValidationMessage("请先选择一个监控文件夹。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(GlobalPassword))
        {
            SetValidationMessage("要保护文件夹，请先填写全局密码。");
            return false;
        }

        folder.Locked = true;
        RefreshWatchedFolderCollection(folder);
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        return true;
    }

    public bool UnprotectSelectedFolder()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            SetValidationMessage("请先选择一个监控文件夹。");
            return false;
        }

        folder.Locked = false;
        RefreshWatchedFolderCollection(folder);
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        return true;
    }

    public bool HasProtectedFoldersWithoutPassword()
    {
        return WatchedFolders.Any(folder => folder.Locked) && string.IsNullOrWhiteSpace(GlobalPassword);
    }

    public IReadOnlyList<WatchedFolder> GetWatchedFolders()
    {
        return WatchedFolders.ToList();
    }

    private void WatchedFoldersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshWatchedFolderStatus();
    }

    private void RefreshWatchedFolderStatus()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            WatchedFolderStatusText.Text = "选中文件夹后，可设置是否受密码保护。";
            return;
        }

        WatchedFolderStatusText.Text = folder.Locked
            ? $"当前状态：受保护。运行时可在“锁定管理”里输入密码解锁 {Path.GetFileName(folder.Path)}。"
            : "当前状态：未受保护。该文件夹中的媒体始终可见。";
    }

    private void RefreshWatchedFolderCollection(WatchedFolder selectedFolder)
    {
        WatchedFoldersList.ItemsSource = null;
        WatchedFoldersList.ItemsSource = WatchedFolders;
        WatchedFoldersList.SelectedItem = selectedFolder;
    }
}
