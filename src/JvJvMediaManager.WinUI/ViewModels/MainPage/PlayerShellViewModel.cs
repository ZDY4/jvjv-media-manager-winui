using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.ViewModels.MainPage;

public sealed class NumpadTagShortcutHintItem
{
    private static readonly Brush DefaultBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(0, 0, 0, 0));
    private static readonly Brush AppliedBackgroundBrush = new SolidColorBrush(ColorHelper.FromArgb(64, 255, 255, 255));

    public required int Digit { get; init; }

    public required string Tag { get; init; }

    public required bool IsApplied { get; init; }

    public string DisplayText => $"{Digit}  {Tag}";

    public Brush BackgroundBrush => IsApplied ? AppliedBackgroundBrush : DefaultBackgroundBrush;
}

public sealed class PlayerMediaTagItem
{
    public required string Tag { get; init; }
}

public sealed class PlayerShellViewModel : ObservableObject
{
    private Visibility _emptyStateVisibility = Visibility.Visible;
    private Visibility _playerInfoVisibility = Visibility.Collapsed;
    private string _playerFileName = string.Empty;
    private string _playerResolution = string.Empty;
    private Visibility _shortcutHintVisibility = Visibility.Collapsed;
    private string _shortcutHintEmptyStateText = "未设置数字快捷标签";

    public PlayerShellViewModel(SelectionViewModel selection)
    {
        Selection = selection;
        VideoPlayback = new VideoPlaybackViewModel();
        ImagePreview = new ImagePreviewViewModel();
        ClipEditor = new ClipEditorViewModel();
        ShortcutHintItems.CollectionChanged += ShortcutHintItems_CollectionChanged;
        PlayerTags.CollectionChanged += PlayerTags_CollectionChanged;
    }

    public SelectionViewModel Selection { get; }

    public VideoPlaybackViewModel VideoPlayback { get; }

    public ImagePreviewViewModel ImagePreview { get; }

    public ClipEditorViewModel ClipEditor { get; }

    public ObservableCollection<NumpadTagShortcutHintItem> ShortcutHintItems { get; } = new();

    public ObservableCollection<PlayerMediaTagItem> PlayerTags { get; } = new();

    public Visibility EmptyStateVisibility
    {
        get => _emptyStateVisibility;
        set => SetProperty(ref _emptyStateVisibility, value);
    }

    public Visibility PlayerInfoVisibility
    {
        get => _playerInfoVisibility;
        set => SetProperty(ref _playerInfoVisibility, value);
    }

    public string PlayerFileName
    {
        get => _playerFileName;
        set => SetProperty(ref _playerFileName, value);
    }

    public string PlayerResolution
    {
        get => _playerResolution;
        set => SetProperty(ref _playerResolution, value);
    }

    public Visibility ShortcutHintVisibility
    {
        get => _shortcutHintVisibility;
        set => SetProperty(ref _shortcutHintVisibility, value);
    }

    public string ShortcutHintEmptyStateText
    {
        get => _shortcutHintEmptyStateText;
        set => SetProperty(ref _shortcutHintEmptyStateText, value);
    }

    public Visibility ShortcutHintItemsVisibility => ShortcutHintItems.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ShortcutHintEmptyStateVisibility => ShortcutHintItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PlayerTagsVisibility => PlayerTags.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    private void ShortcutHintItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShortcutHintItemsVisibility));
        OnPropertyChanged(nameof(ShortcutHintEmptyStateVisibility));
    }

    private void PlayerTags_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PlayerTagsVisibility));
    }
}
