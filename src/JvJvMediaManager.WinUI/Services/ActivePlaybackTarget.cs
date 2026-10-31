using JvJvMediaManager.Utilities;
using Windows.System;

namespace JvJvMediaManager.Services;

internal enum ActivePlaybackTargetKind
{
    Main,
    Docked,
    Independent
}

internal interface IIndependentPlaybackTarget
{
    void PlayMedia(IndependentMediaWindowMedia media);

    Task<bool> TryHandleShortcutAsync(VirtualKey key);
}

/// <summary>
/// Tracks which playback surface should receive library selection and player shortcuts
/// after the user last clicked/activated it.
/// </summary>
internal sealed class ActivePlaybackTarget
{
    private readonly object _gate = new();
    private ActivePlaybackTargetKind _kind = ActivePlaybackTargetKind.Main;
    private WeakReference<IIndependentPlaybackTarget>? _independent;

    public ActivePlaybackTargetKind Kind
    {
        get
        {
            lock (_gate)
            {
                return ResolveKindUnlocked();
            }
        }
    }

    public IIndependentPlaybackTarget? IndependentTarget
    {
        get
        {
            lock (_gate)
            {
                return ResolveIndependentUnlocked();
            }
        }
    }

    public void SetMain()
    {
        lock (_gate)
        {
            _kind = ActivePlaybackTargetKind.Main;
            _independent = null;
        }

        AppTraceLogger.Log("ActivePlaybackTarget", "Target=Main.");
    }

    public void SetDocked()
    {
        lock (_gate)
        {
            _kind = ActivePlaybackTargetKind.Docked;
            _independent = null;
        }

        AppTraceLogger.Log("ActivePlaybackTarget", "Target=Docked.");
    }

    public void SetIndependent(IIndependentPlaybackTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _kind = ActivePlaybackTargetKind.Independent;
            _independent = new WeakReference<IIndependentPlaybackTarget>(target);
        }

        AppTraceLogger.Log("ActivePlaybackTarget", "Target=Independent.");
    }

    public void ClearDockedIfActive()
    {
        lock (_gate)
        {
            if (_kind != ActivePlaybackTargetKind.Docked)
            {
                return;
            }

            _kind = ActivePlaybackTargetKind.Main;
            _independent = null;
        }

        AppTraceLogger.Log("ActivePlaybackTarget", "Docked cleared; Target=Main.");
    }

    public void ClearIndependentIfActive(IIndependentPlaybackTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_kind != ActivePlaybackTargetKind.Independent)
            {
                return;
            }

            if (!ReferenceEquals(ResolveIndependentUnlocked(), target))
            {
                return;
            }

            _kind = ActivePlaybackTargetKind.Main;
            _independent = null;
        }

        AppTraceLogger.Log("ActivePlaybackTarget", "Independent cleared; Target=Main.");
    }

    private ActivePlaybackTargetKind ResolveKindUnlocked()
    {
        if (_kind == ActivePlaybackTargetKind.Independent && ResolveIndependentUnlocked() == null)
        {
            _kind = ActivePlaybackTargetKind.Main;
            _independent = null;
        }

        return _kind;
    }

    private IIndependentPlaybackTarget? ResolveIndependentUnlocked()
    {
        if (_independent == null)
        {
            return null;
        }

        return _independent.TryGetTarget(out var window) ? window : null;
    }
}
