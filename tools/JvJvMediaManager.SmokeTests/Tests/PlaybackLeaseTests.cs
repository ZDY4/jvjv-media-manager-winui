using JvJvMediaManager.Services;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class PlaybackLeaseTests
{
    private const string Suite = "playback-lease";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "no leases means inactive", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            Assert.False(coordinator.IsPlaybackActive, "No leases -> inactive");
            Assert.Equal(0, coordinator.ActiveLeaseCount, "No leases counted");
        });

        registry.Add(Suite, "acquiring a lease and setting playing makes playback active", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            using var lease = coordinator.Acquire("main");
            Assert.Equal(1, coordinator.ActiveLeaseCount, "One active lease");
            Assert.False(coordinator.IsPlaybackActive, "Opening/inactive lease is not active yet");

            lease.SetState(PlaybackActivityState.Playing);
            Assert.True(coordinator.IsPlaybackActive, "Playing lease makes playback active");
        });

        // WP-04 core invariant: releasing one overlapping lease must not clear
        // global playback state while another lease is still playing.
        registry.Add(Suite, "releasing one overlapping lease keeps global state", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            var main = coordinator.Acquire("main");
            var window = coordinator.Acquire("independent-1");

            main.SetState(PlaybackActivityState.Playing);
            window.SetState(PlaybackActivityState.Playing);
            Assert.True(coordinator.IsPlaybackActive, "Both playing -> active");

            main.Dispose();
            Assert.True(coordinator.IsPlaybackActive, "Still active because other lease is playing");
            Assert.Equal(1, coordinator.ActiveLeaseCount, "One lease remains");

            window.Dispose();
            Assert.False(coordinator.IsPlaybackActive, "All leases released -> inactive");
            Assert.Equal(0, coordinator.ActiveLeaseCount, "No leases remain");
        });

        registry.Add(Suite, "pausing a lease deactivates when no others play", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            using var lease = coordinator.Acquire("main");
            lease.SetState(PlaybackActivityState.Playing);
            Assert.True(coordinator.IsPlaybackActive, "Playing -> active");

            lease.SetState(PlaybackActivityState.Inactive);
            Assert.False(coordinator.IsPlaybackActive, "Paused/inactive -> not active");
        });

        registry.Add(Suite, "buffering counts as active playback", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            using var lease = coordinator.Acquire("main");
            lease.SetState(PlaybackActivityState.Buffering);
            Assert.True(coordinator.IsPlaybackActive, "Buffering -> active");
        });

        registry.Add(Suite, "double dispose of a lease is safe", () =>
        {
            using var coordinator = new PlaybackResourceCoordinator();
            var lease = coordinator.Acquire("main");
            lease.SetState(PlaybackActivityState.Playing);
            lease.Dispose();
            lease.Dispose();
            Assert.Equal(0, coordinator.ActiveLeaseCount, "Double dispose does not double-count");
            Assert.False(coordinator.IsPlaybackActive, "Inactive after dispose");
        });
    }
}
