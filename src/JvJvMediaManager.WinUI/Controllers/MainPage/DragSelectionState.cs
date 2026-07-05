using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class DragSelectionState
{
    public bool IsPending { get; set; }

    public bool IsSelecting { get; set; }

    public bool IsAdditive { get; set; }

    public ListViewBase? View { get; set; }

    public UIElement? CaptureOwner { get; set; }

    public Point StartPoint { get; set; }

    public Point CurrentPointerPoint { get; set; }

    public double AutoScrollVelocity { get; set; }

    public HashSet<string> SeedIds { get; set; } = new(StringComparer.Ordinal);

    public string? SeedPrimaryId { get; set; }

    public HashSet<string> OriginalIds { get; set; } = new(StringComparer.Ordinal);

    public string? OriginalPrimaryId { get; set; }

    public bool StartedOnItem { get; set; }

    public string? PressedMediaId { get; set; }

    public bool IsActive => IsPending || IsSelecting;

    public void Reset()
    {
        IsPending = false;
        IsSelecting = false;
        IsAdditive = false;
        View = null;
        CaptureOwner = null;
        StartPoint = default;
        CurrentPointerPoint = default;
        AutoScrollVelocity = 0;
        SeedIds.Clear();
        SeedPrimaryId = null;
        OriginalIds.Clear();
        OriginalPrimaryId = null;
        StartedOnItem = false;
        PressedMediaId = null;
    }
}
