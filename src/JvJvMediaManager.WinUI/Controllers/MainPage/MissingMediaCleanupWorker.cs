using JvJvMediaManager.Utilities;
using JvJvMediaManager.ViewModels;

namespace JvJvMediaManager.Controllers.MainPage;

internal sealed class MissingMediaCleanupWorker
{
    // Only the destructive "confirmed missing" outcome is retried: a transient
    // failure never deletes anything, so retrying it would just add latency.
    private static readonly TimeSpan[] MissingConfirmationDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    ];

    private readonly object _sync = new();
    private readonly HashSet<string> _queuedIds = new(StringComparer.Ordinal);
    private readonly Action<MediaItemViewModel, string> _logMissingMedia;
    private int _activeCount;

    public MissingMediaCleanupWorker(Action<MediaItemViewModel, string> logMissingMedia)
    {
        _logMissingMedia = logMissingMedia;
    }

    public bool IsActive => Volatile.Read(ref _activeCount) > 0;

    public bool TryQueue(MediaItemViewModel media, string source)
    {
        lock (_sync)
        {
            if (!_queuedIds.Add(media.Id))
            {
                return false;
            }
        }

        _logMissingMedia(media, source);
        return true;
    }

    public void ReleaseQueued(MediaItemViewModel media)
    {
        lock (_sync)
        {
            _queuedIds.Remove(media.Id);
        }
    }

    /// <summary>
    /// Probes the media file and classifies the outcome. A single
    /// "not found" is re-checked a couple of times before it is reported as
    /// <see cref="MediaFileAvailability.Missing"/>, so a momentary volume or
    /// filter-driver hiccup cannot masquerade as a deleted file.
    /// </summary>
    public async Task<MediaFileAvailability> ProbeMediaFileAsync(MediaItemViewModel media)
    {
        var availability = await Task.Run(() => ProbeMediaFile(media)).ConfigureAwait(false);
        for (var attempt = 0; attempt < MissingConfirmationDelays.Length && availability == MediaFileAvailability.Missing; attempt++)
        {
            await Task.Delay(MissingConfirmationDelays[attempt]).ConfigureAwait(false);
            availability = await Task.Run(() => ProbeMediaFile(media)).ConfigureAwait(false);
        }

        return availability;
    }

    public IDisposable EnterCleanup()
    {
        Interlocked.Increment(ref _activeCount);
        return new CleanupScope(this);
    }

    private static MediaFileAvailability ProbeMediaFile(MediaItemViewModel media)
    {
        var result = MediaFileProbe.Probe(media.FileSystemPath);
        if (result.Availability == MediaFileAvailability.Unavailable && result.Win32Error != 0)
        {
            AppTraceLogger.LogSampled(
                "MissingMediaCleanup",
                $"media-probe-transient:{result.Win32Error}",
                $"Media file probe hit a transient storage failure; keeping the library entry. MediaId='{media.Id}', Path='{media.FileSystemPath}', Win32Error={result.Win32Error}.",
                TimeSpan.FromSeconds(5));
        }

        return result.Availability;
    }

    private sealed class CleanupScope : IDisposable
    {
        private MissingMediaCleanupWorker? _owner;

        public CleanupScope(MissingMediaCleanupWorker owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
            {
                Interlocked.Decrement(ref owner._activeCount);
            }
        }
    }
}
