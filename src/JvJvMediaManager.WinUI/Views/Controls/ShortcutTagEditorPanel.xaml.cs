using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace JvJvMediaManager.Views.Controls;

public sealed partial class ShortcutTagEditorPanel : UserControl
{
    private const int ShortcutKeyCount = 9;
    private ShortcutTagRow? _draggedRow;
    private bool _isReorderingTags;

    public ShortcutTagEditorPanel(IReadOnlyList<string> shortcuts)
    {
        var rowCount = Math.Max(ShortcutKeyCount, shortcuts.Count);
        for (var index = 0; index < rowCount; index++)
        {
            Rows.Add(new ShortcutTagRow(index < shortcuts.Count ? shortcuts[index] : string.Empty));
        }

        RefreshRowPositions();
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    public event EventHandler? ShortcutsChanged;

    public ObservableCollection<ShortcutTagRow> Rows { get; } = new();

    public IReadOnlyList<string> GetShortcuts()
    {
        return Rows.Select(row => row.Tag.Trim()).ToList();
    }

    private void AddRowButton_Click(object sender, RoutedEventArgs e)
    {
        var row = new ShortcutTagRow(string.Empty);
        Rows.Add(row);
        RefreshRowPositions();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);

        ShortcutRowsList.ScrollIntoView(row);
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (ShortcutRowsList.ContainerFromItem(row) is ListViewItem container)
            {
                container.Focus(FocusState.Programmatic);
            }
        });
    }

    private void RemoveRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutTagRow row })
        {
            return;
        }

        var index = Rows.IndexOf(row);
        if (index < ShortcutKeyCount)
        {
            return;
        }

        Rows.RemoveAt(index);
        RefreshRowPositions();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TagTextBox_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not TextBox { Tag: ShortcutTagRow row })
        {
            args.Cancel = true;
            return;
        }

        _draggedRow = row;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(row.Tag);
    }

    private void TagTextBox_DragOver(object sender, DragEventArgs e)
    {
        if (_draggedRow != null && sender is TextBox { Tag: ShortcutTagRow targetRow }
            && !ReferenceEquals(_draggedRow, targetRow))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "移动 Tag";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private void TagTextBox_Drop(object sender, DragEventArgs e)
    {
        if (_draggedRow == null || sender is not TextBox { Tag: ShortcutTagRow targetRow })
        {
            return;
        }

        var sourceIndex = Rows.IndexOf(_draggedRow);
        var targetIndex = Rows.IndexOf(targetRow);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            _draggedRow = null;
            return;
        }

        _isReorderingTags = true;
        try
        {
            var tag = _draggedRow.Tag;
            if (sourceIndex < targetIndex)
            {
                for (var index = sourceIndex; index < targetIndex; index++)
                {
                    Rows[index].Tag = Rows[index + 1].Tag;
                }
            }
            else
            {
                for (var index = sourceIndex; index > targetIndex; index--)
                {
                    Rows[index].Tag = Rows[index - 1].Tag;
                }
            }

            Rows[targetIndex].Tag = tag;
        }
        finally
        {
            _isReorderingTags = false;
        }

        _draggedRow = null;
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TagTextBox_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        _draggedRow = null;
    }

    private void TagTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReorderingTags)
        {
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshRowPositions()
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            Rows[index].SetPosition(index, ShortcutKeyCount);
        }
    }
}

public sealed class ShortcutTagRow : INotifyPropertyChanged
{
    private string _tag;
    private string _positionLabel = string.Empty;
    private Visibility _removeButtonVisibility;

    public ShortcutTagRow(string tag)
    {
        _tag = tag;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Tag
    {
        get => _tag;
        set => SetProperty(ref _tag, value);
    }

    public string PositionLabel
    {
        get => _positionLabel;
        private set => SetProperty(ref _positionLabel, value);
    }

    public Visibility RemoveButtonVisibility
    {
        get => _removeButtonVisibility;
        private set => SetProperty(ref _removeButtonVisibility, value);
    }

    internal void SetPosition(int index, int shortcutKeyCount)
    {
        PositionLabel = index < shortcutKeyCount ? $"小键盘 {index + 1}" : "无快捷键";
        RemoveButtonVisibility = index < shortcutKeyCount ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
