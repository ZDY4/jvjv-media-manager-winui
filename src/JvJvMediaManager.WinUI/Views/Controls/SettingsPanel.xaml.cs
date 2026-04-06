using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using JvJvMediaManager.Models;
using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels.MainPage;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class SettingsPanel : UserControl
{
    private const string ClearWatchedFoldersButtonDefaultText = "清空";
    private const string ClearWatchedFoldersButtonConfirmText = "再次确认";
    private const string ClearWatchedFoldersConfirmationMessage = "再次点击“再次确认”以清空监控文件夹。";
    private readonly TextBox[] _numpadTagTextBoxes;
    private bool _clearWatchedFoldersConfirmationPending;

    public event EventHandler? PortableModeCommitted;

    public event EventHandler? DataDirectoryCommitted;

    public event EventHandler? GlobalPasswordCommitted;

    public event EventHandler? NumpadTagShortcutsCommitted;

    public event EventHandler? WatchedFoldersChanged;

    public ObservableCollection<WatchedFolder> WatchedFolders { get; }

    public SettingsPanel(LibraryShellViewModel viewModel)
    {
        InitializeComponent();

        _numpadTagTextBoxes =
        [
            NumpadTag1TextBox,
            NumpadTag2TextBox,
            NumpadTag3TextBox,
            NumpadTag4TextBox,
            NumpadTag5TextBox,
            NumpadTag6TextBox,
            NumpadTag7TextBox,
            NumpadTag8TextBox,
            NumpadTag9TextBox
        ];

        PortableToggle.IsOn = viewModel.PortableMode;
        DataDirTextBox.Text = viewModel.ConfiguredDataDir ?? string.Empty;
        GlobalPasswordBox.Password = viewModel.LockPassword;
        for (var i = 0; i < _numpadTagTextBoxes.Length; i++)
        {
            _numpadTagTextBoxes[i].Text = i < viewModel.NumpadTagShortcuts.Count
                ? viewModel.NumpadTagShortcuts[i]
                : string.Empty;
        }

        WatchedFolders = new ObservableCollection<WatchedFolder>(
            viewModel.WatchedFolders.Select(item => new WatchedFolder
            {
                Path = item.Path,
                Locked = item.Locked,
                Visible = item.Visible
            }));

        WatchedFoldersList.ItemsSource = WatchedFolders;
        WatchedFoldersList.SelectionChanged += WatchedFoldersList_SelectionChanged;
        PortableToggle.Toggled += PortableToggle_Toggled;
        DataDirTextBox.LostFocus += DataDirTextBox_LostFocus;
        GlobalPasswordBox.LostFocus += GlobalPasswordBox_LostFocus;
        foreach (var textBox in _numpadTagTextBoxes)
        {
            textBox.LostFocus += NumpadTagTextBox_LostFocus;
        }

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

    public WatchedFolder? SelectedWatchedFolder => WatchedFoldersList.SelectedItem as WatchedFolder;

    public bool IsClearWatchedFoldersConfirmationPending => _clearWatchedFoldersConfirmationPending;

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

    public void CommitDataDirectory()
    {
        DataDirectoryCommitted?.Invoke(this, EventArgs.Empty);
    }

    public void CommitGlobalPassword()
    {
        GlobalPasswordCommitted?.Invoke(this, EventArgs.Empty);
    }

    public bool TryAddWatchedFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)
            || WatchedFolders.Any(item => string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        var watchedFolder = new WatchedFolder { Path = folder, Locked = false };
        WatchedFolders.Add(watchedFolder);
        WatchedFoldersList.SelectedItem = watchedFolder;
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        AppTraceLogger.Log("SettingsPanel", $"Added watched folder '{folder}'. Count={WatchedFolders.Count}.");
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void RemoveSelectedWatchedFolder()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            return;
        }

        RemoveWatchedFolder(folder);
    }

    public void ClearWatchedFolders()
    {
        AppTraceLogger.Log("SettingsPanel", $"ClearWatchedFolders starting. ExistingCount={WatchedFolders.Count}, Selected='{SelectedWatchedFolder?.Path ?? "<null>"}'.");
        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        WatchedFolders.Clear();
        WatchedFoldersList.SelectedIndex = -1;
        RefreshWatchedFolderStatus();
        AppTraceLogger.Log("SettingsPanel", "ClearWatchedFolders completed. Count=0.");
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BeginClearWatchedFoldersConfirmation()
    {
        _clearWatchedFoldersConfirmationPending = true;
        ClearWatchedFoldersButton.Content = ClearWatchedFoldersButtonConfirmText;
        SetValidationMessage(ClearWatchedFoldersConfirmationMessage);
        AppTraceLogger.Log("SettingsPanel", $"Clear watched folders confirmation armed. Count={WatchedFolders.Count}.");
    }

    public void ResetClearWatchedFoldersConfirmation(bool clearValidationMessage)
    {
        _clearWatchedFoldersConfirmationPending = false;
        ClearWatchedFoldersButton.Content = ClearWatchedFoldersButtonDefaultText;
        if (clearValidationMessage && string.Equals(ValidationText.Text, ClearWatchedFoldersConfirmationMessage, StringComparison.Ordinal))
        {
            ClearValidationMessage();
        }
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
        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        RefreshWatchedFolderCollection(folder);
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
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
        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        RefreshWatchedFolderCollection(folder);
        ClearValidationMessage();
        RefreshWatchedFolderStatus();
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
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

    public IReadOnlyList<string> GetNumpadTagShortcuts()
    {
        return _numpadTagTextBoxes
            .Select(textBox => textBox.Text.Trim())
            .ToList();
    }

    private void WatchedFoldersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AppTraceLogger.Log("SettingsPanel", $"WatchedFoldersList selection changed. Selected='{SelectedWatchedFolder?.Path ?? "<null>"}', Count={WatchedFolders.Count}.");
        RefreshWatchedFolderStatus();
    }

    private void PortableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        PortableModeCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void DataDirTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        DataDirectoryCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void GlobalPasswordBox_LostFocus(object sender, RoutedEventArgs e)
    {
        GlobalPasswordCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void NumpadTagTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        NumpadTagShortcutsCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveWatchedFolderItemButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WatchedFolder folder })
        {
            return;
        }

        RemoveWatchedFolder(folder);
    }

    private void ToggleWatchedFolderVisibilityButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: WatchedFolder folder } button)
        {
            return;
        }

        UpdateToggleVisibilityButtonState(button, folder.Visible);
    }

    private void ToggleWatchedFolderVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: WatchedFolder folder } button)
        {
            return;
        }

        UpdateToggleVisibilityButtonState(button, folder.Visible);
        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        AppTraceLogger.Log("SettingsPanel", $"Toggled watched folder visibility. Path='{folder.Path}', Visible={folder.Visible}.");
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateToggleVisibilityButtonState(ToggleButton button, bool visible)
    {
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = visible ? "\uE7BD" : "\uE7BA";
            icon.Opacity = visible ? 1.0 : 0.5;
        }

        ToolTipService.SetToolTip(button, visible ? "点击隐藏此文件夹" : "点击显示此文件夹");
    }

    private void ToggleWatchedFolderLockedButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: WatchedFolder folder } button)
        {
            return;
        }

        UpdateToggleLockedButtonState(button, folder.Locked);
    }

    private void ToggleWatchedFolderLockedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: WatchedFolder folder } button)
        {
            return;
        }

        UpdateToggleLockedButtonState(button, folder.Locked);
        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        AppTraceLogger.Log("SettingsPanel", $"Toggled watched folder protection. Path='{folder.Path}', Locked={folder.Locked}.");
        RefreshWatchedFolderStatus();
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateToggleLockedButtonState(ToggleButton button, bool locked)
    {
        if (button.Content is FontIcon icon)
        {
            icon.Glyph = locked ? "\uE72E" : "\uE785";
            icon.Opacity = locked ? 1.0 : 0.5;
        }

        ToolTipService.SetToolTip(button, locked ? "点击取消保护" : "点击设为受保护");
    }

    private void RefreshWatchedFolderStatus()
    {
        if (SelectedWatchedFolder is not WatchedFolder folder)
        {
            WatchedFolderStatusText.Text = "选中文件夹后，可设置是否受密码保护或是否在媒体库中显示。";
            return;
        }

        var statusParts = new List<string>();

        if (!folder.Visible)
        {
            statusParts.Add("已隐藏");
        }

        if (folder.Locked)
        {
            statusParts.Add("受保护");
        }

        var statusText = statusParts.Count == 0
            ? "未受保护"
            : string.Join("、", statusParts);

        var instructionText = folder.Locked
            ? $"运行时可在\u201C锁定管理\u201D里输入密码解锁 {Path.GetFileName(folder.Path)}。"
            : folder.Visible
                ? "该文件夹中的媒体在媒体库中可见。"
                : "该文件夹中的媒体已从媒体库隐藏，不会在扫描时处理。";

        WatchedFolderStatusText.Text = $"当前状态：{statusText}。{instructionText}";
    }

    private void RefreshWatchedFolderCollection(WatchedFolder selectedFolder)
    {
        WatchedFoldersList.ItemsSource = null;
        WatchedFoldersList.ItemsSource = WatchedFolders;
        WatchedFoldersList.SelectedItem = selectedFolder;
    }

    private void RemoveWatchedFolder(WatchedFolder folder)
    {
        var removedIndex = WatchedFolders.IndexOf(folder);
        if (removedIndex < 0)
        {
            return;
        }

        ResetClearWatchedFoldersConfirmation(clearValidationMessage: true);
        var wasSelected = ReferenceEquals(SelectedWatchedFolder, folder);
        WatchedFolders.RemoveAt(removedIndex);

        if (WatchedFolders.Count == 0)
        {
            WatchedFoldersList.SelectedIndex = -1;
        }
        else if (wasSelected)
        {
            WatchedFoldersList.SelectedIndex = Math.Min(removedIndex, WatchedFolders.Count - 1);
        }

        AppTraceLogger.Log("SettingsPanel", $"Removed watched folder '{folder.Path}'. RemainingCount={WatchedFolders.Count}.");
        RefreshWatchedFolderStatus();
        WatchedFoldersChanged?.Invoke(this, EventArgs.Empty);
    }
}
