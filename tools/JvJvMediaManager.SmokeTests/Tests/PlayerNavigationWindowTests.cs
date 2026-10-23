using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.SmokeTests.Harness;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class PlayerNavigationWindowTests
{
    private const string Suite = "player-navigation";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "empty focus yields empty window", () =>
        {
            var items = RangeIds(0, 10);
            var window = PlayerNavigationWindow.SliceAround(items, Array.Empty<string>(), static id => id, radius: 3);
            Assert.Equal(0, window.Count, "No focus items should produce an empty window");
        });

        registry.Add(Suite, "window around a middle item is radius on each side", () =>
        {
            var items = RangeIds(0, 20);
            var window = PlayerNavigationWindow.SliceAround(items, new[] { "id-10" }, static id => id, radius: 2);
            AssertEqualIds(window, "id-8", "id-9", "id-10", "id-11", "id-12");
        });

        registry.Add(Suite, "window clamps at the start of the list", () =>
        {
            var items = RangeIds(0, 8);
            var window = PlayerNavigationWindow.SliceAround(items, new[] { "id-0" }, static id => id, radius: 3);
            AssertEqualIds(window, "id-0", "id-1", "id-2", "id-3");
        });

        registry.Add(Suite, "window clamps at the end of the list", () =>
        {
            var items = RangeIds(0, 8);
            var window = PlayerNavigationWindow.SliceAround(items, new[] { "id-7" }, static id => id, radius: 3);
            AssertEqualIds(window, "id-4", "id-5", "id-6", "id-7");
        });

        registry.Add(Suite, "far-apart focus items do not include the gap", () =>
        {
            var items = RangeIds(0, 30);
            var window = PlayerNavigationWindow.SliceAround(items, new[] { "id-2", "id-27" }, static id => id, radius: 1);
            AssertEqualIds(window, "id-1", "id-2", "id-3", "id-26", "id-27", "id-28");
            Assert.False(window.Contains("id-15"), "Items between disjoint windows must not be copied");
        });

        registry.Add(Suite, "focus items missing from navigation are appended", () =>
        {
            var items = RangeIds(0, 5);
            var window = PlayerNavigationWindow.SliceAround(
                items,
                new[] { "id-1", "extra-a" },
                static id => id,
                radius: 1);
            AssertEqualIds(window, "id-0", "id-1", "id-2", "extra-a");
        });

        registry.Add(Suite, "default radius keeps a large library snapshot bounded", () =>
        {
            var items = RangeIds(0, 10_000);
            var window = PlayerNavigationWindow.SliceAround(
                items,
                new[] { "id-5000" },
                static id => id);
            Assert.Equal(
                (PlayerNavigationWindow.DefaultRadius * 2) + 1,
                window.Count,
                "A single focus in a large list should be 2*radius+1");
            Assert.Equal("id-4936", window[0], "Window should start at focus-radius");
            Assert.Equal("id-5064", window[^1], "Window should end at focus+radius");
        });

        registry.Add(Suite, "ToMediaFile uses snapshot metadata and does not require the file", () =>
        {
            var snapshot = new IndependentMediaWindowMedia(
                Id: "media-1",
                FileSystemPath: @"C:\missing\does-not-exist.mp4",
                FileName: "does-not-exist.mp4",
                Type: MediaType.Video,
                ResolutionText: "1920x1080",
                ContentHash: "abc",
                FingerprintVersion: 2,
                Tags: new[] { "tag-a", "tag-b" },
                Rotation: 90,
                Size: 12345,
                ModifiedAt: 1_700_000_000,
                Duration: 12.5,
                Width: 1920,
                Height: 1080);

            var file = snapshot.ToMediaFile();
            Assert.Equal("media-1", file.Id, "Id");
            Assert.Equal(12345, file.Size, "Size comes from the snapshot, not FileInfo");
            Assert.Equal(1_700_000_000, file.ModifiedAt, "ModifiedAt comes from the snapshot");
            Assert.Equal(12.5, file.Duration.GetValueOrDefault(), "Duration");
            Assert.Equal(1920, file.Width.GetValueOrDefault(), "Width");
            Assert.Equal(1080, file.Height.GetValueOrDefault(), "Height");
            Assert.Equal(90, file.Rotation, "Rotation");
            Assert.Equal("abc", file.ContentHash, "ContentHash");
        });

        registry.Add(Suite, "overlapping windows and duplicate focus preserve first occurrence and order", () =>
        {
            var items = new[] { "a", "b", "c", "d", "b", "e", "f" };
            var focus = new[] { "d", "b", "missing", "b", "missing" };
            AssertEqualIds(PlayerNavigationWindow.SliceAround(items, focus, static id => id, radius: 1),
                "a", "b", "c", "d", "missing");
        });

        registry.Add(Suite, "maximum radius does not overflow", () =>
        {
            var items = RangeIds(0, 8);
            Assert.SequenceEqual(items,
                PlayerNavigationWindow.SliceAround(items, new[] { "id-5" }, static id => id, int.MaxValue),
                "An unlimited radius includes the entire library");
        });

        registry.Add(Suite, "randomized windows match the reference selection contract", () =>
        {
            var random = new Random(42);
            for (var iteration = 0; iteration < 250; iteration++)
            {
                var items = Enumerable.Range(0, random.Next(0, 100))
                    .Select(_ => $"id-{random.Next(0, 60)}").ToList();
                var focus = Enumerable.Range(0, random.Next(0, 12))
                    .Select(_ => $"id-{random.Next(0, 80)}").ToArray();
                var radius = random.Next(-2, 15);
                var indices = focus.Select(id => items.IndexOf(id)).Where(index => index >= 0).ToArray();
                var expected = items.Where((_, index) => indices.Any(focusIndex => Math.Abs(index - focusIndex) <= Math.Max(0, radius)))
                    .Concat(focus.Where(id => !items.Contains(id)))
                    .Distinct(StringComparer.Ordinal).ToArray();
                Assert.SequenceEqual(expected,
                    PlayerNavigationWindow.SliceAround(items, focus, static id => id, radius),
                    $"Reference selection, iteration {iteration}");
            }
        });

        registry.Add(Suite, "100k library navigation allocates only for the selected window", () =>
        {
            var items = RangeIds(0, 100_000);
            var focus = new[] { "id-50000" };
            Func<string, string> getId = static id => id;
            _ = PlayerNavigationWindow.SliceAround(items, focus, getId);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var window = PlayerNavigationWindow.SliceAround(items, focus, getId);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(129, window.Count, "Large library still returns the complete neighborhood");
            Assert.True(allocated < 64 * 1024, $"Navigation must not allocate a full-library index; allocated {allocated:N0} bytes");
            Console.WriteLine($"    100k navigation allocation: {allocated:N0} bytes, {window.Count} returned items");
        });
    }

    private static List<string> RangeIds(int start, int count)
    {
        return Enumerable.Range(start, count).Select(i => $"id-{i}").ToList();
    }

    private static void AssertEqualIds(IReadOnlyList<string> actual, params string[] expected)
    {
        Assert.Equal(
            string.Join(",", expected),
            string.Join(",", actual),
            "Navigation window ids");
    }
}
