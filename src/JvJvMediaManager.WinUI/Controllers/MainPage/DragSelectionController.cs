using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class DragSelectionController
{
    private readonly DragSelectionState _state = new();
    private readonly double _activationThreshold;
    private readonly double _autoScrollEdgeThreshold;
    private readonly double _autoScrollMaxStep;

    public DragSelectionController(
        double activationThreshold,
        double autoScrollEdgeThreshold,
        double autoScrollMaxStep)
    {
        _activationThreshold = activationThreshold;
        _autoScrollEdgeThreshold = autoScrollEdgeThreshold;
        _autoScrollMaxStep = autoScrollMaxStep;
    }

    public DragSelectionState State => _state;

    public void Begin(
        ListViewBase view,
        Point startPoint,
        bool isAdditive,
        HashSet<string> originalIds,
        string? originalPrimaryId,
        bool startedOnItem,
        string? pressedMediaId)
    {
        End();
        _state.View = view;
        _state.CaptureOwner = view;
        _state.StartPoint = startPoint;
        _state.CurrentPointerPoint = startPoint;
        _state.AutoScrollVelocity = 0;
        _state.IsPending = true;
        _state.IsSelecting = false;
        _state.IsAdditive = isAdditive;
        _state.OriginalIds = originalIds;
        _state.OriginalPrimaryId = originalPrimaryId;
        _state.SeedIds = isAdditive
            ? originalIds.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        _state.SeedPrimaryId = isAdditive ? originalPrimaryId : null;
        _state.StartedOnItem = startedOnItem;
        _state.PressedMediaId = pressedMediaId;
    }

    public bool HasExceededActivationThreshold(Point currentPoint)
    {
        var deltaX = currentPoint.X - _state.StartPoint.X;
        var deltaY = currentPoint.Y - _state.StartPoint.Y;
        return Math.Abs(deltaX) >= _activationThreshold || Math.Abs(deltaY) >= _activationThreshold;
    }

    public double CalculateAutoScrollVelocity(Point rawPoint, double viewportHeight)
    {
        if (!_state.IsSelecting || viewportHeight <= 0)
        {
            return 0;
        }

        if (rawPoint.Y <= _autoScrollEdgeThreshold)
        {
            var intensity = 1 - (Math.Max(rawPoint.Y, 0) / _autoScrollEdgeThreshold);
            return -Math.Max(4, _autoScrollMaxStep * intensity);
        }

        if (rawPoint.Y >= viewportHeight - _autoScrollEdgeThreshold)
        {
            var distanceToEdge = Math.Max(0, viewportHeight - rawPoint.Y);
            var intensity = 1 - (distanceToEdge / _autoScrollEdgeThreshold);
            return Math.Max(4, _autoScrollMaxStep * intensity);
        }

        return 0;
    }

    public (HashSet<string> SelectedIds, string? PrimaryId)? BuildPressedItemSelection()
    {
        if (string.IsNullOrWhiteSpace(_state.PressedMediaId))
        {
            return null;
        }

        if (_state.IsAdditive)
        {
            var nextSelection = new HashSet<string>(_state.SeedIds, StringComparer.Ordinal);
            if (!nextSelection.Add(_state.PressedMediaId))
            {
                nextSelection.Remove(_state.PressedMediaId);
            }

            var primaryId = nextSelection.Contains(_state.PressedMediaId)
                ? _state.PressedMediaId
                : _state.SeedPrimaryId;
            return (nextSelection, primaryId);
        }

        return (new HashSet<string>(StringComparer.Ordinal) { _state.PressedMediaId }, _state.PressedMediaId);
    }

    public void End()
    {
        _state.CaptureOwner?.ReleasePointerCaptures();
        _state.Reset();
    }
}
