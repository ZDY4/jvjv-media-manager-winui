using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class MediaFileProbeTests
{
    private const string Suite = "media-file-probe";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "existing file is available", () =>
        {
            using var ws = new TempWorkspace("probe-existing");
            var path = ws.Combine("present.mp4");
            File.WriteAllBytes(path, new byte[16]);

            var result = MediaFileProbe.Probe(path);

            Assert.Equal(MediaFileAvailability.Available, result.Availability, "Existing file is available");
            Assert.Equal(0, result.Win32Error, "Existing file probe reports no Win32 error");
        });

        registry.Add(Suite, "deleted file in a reachable folder is missing", () =>
        {
            using var ws = new TempWorkspace("probe-missing-file");
            var path = ws.Combine("deleted.mp4");

            var result = MediaFileProbe.Probe(path);

            Assert.Equal(MediaFileAvailability.Missing, result.Availability, "Deleted file in an existing folder is missing");
        });

        registry.Add(Suite, "missing file under a missing folder is not a deletion signal", () =>
        {
            using var ws = new TempWorkspace("probe-missing-folder");
            var path = ws.Combine("renamed-folder", "clip.mp4");

            var result = MediaFileProbe.Probe(path);

            Assert.Equal(
                MediaFileAvailability.Unavailable,
                result.Availability,
                "A renamed/deleted folder keeps its entries until a rescan reconciles");
        });

        registry.Add(Suite, "path on a missing volume is not a deletion signal", () =>
        {
            var letter = FindUnusedDriveLetter();
            if (letter == null)
            {
                Console.WriteLine("        (skipped: every drive letter is in use)");
                return;
            }

            var result = MediaFileProbe.Probe($@"{letter}:\jvjvmm-probe\clip.mp4");

            Assert.Equal(
                MediaFileAvailability.Unavailable,
                result.Availability,
                "An offline volume keeps its entries instead of deleting them");
        });

        registry.Add(Suite, "blank path is missing", () =>
        {
            Assert.Equal(MediaFileAvailability.Missing, MediaFileProbe.Probe(null).Availability, "Null path is missing");
            Assert.Equal(MediaFileAvailability.Missing, MediaFileProbe.Probe("   ").Availability, "Blank path is missing");
        });

        registry.Add(Suite, "directory path is not available media", () =>
        {
            using var ws = new TempWorkspace("probe-directory");
            var result = MediaFileProbe.Probe(ws.Root);

            Assert.Equal(MediaFileAvailability.Missing, result.Availability, "A directory path is stale media, not playable");
        });

        registry.Add(Suite, "existing file beyond MAX_PATH is available", () =>
        {
            using var ws = new TempWorkspace("probe-long-present");
            var path = CreateLongMediaPath(ws, "clip.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[16]);

            var result = MediaFileProbe.Probe(path);

            Assert.Equal(MediaFileAvailability.Available, result.Availability, "An existing file past MAX_PATH is available");
            Assert.Equal(0, result.Win32Error, "Long path probe reports no Win32 error");
        });

        registry.Add(Suite, "deleted file beyond MAX_PATH in a reachable folder is missing", () =>
        {
            using var ws = new TempWorkspace("probe-long-missing");
            var path = CreateLongMediaPath(ws, "deleted.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var result = MediaFileProbe.Probe(path);

            Assert.Equal(
                MediaFileAvailability.Missing,
                result.Availability,
                "A deleted file past MAX_PATH is missing when its folder is still there");
        });
    }

    private static string CreateLongMediaPath(TempWorkspace ws, string fileName)
    {
        var segment = new string('a', 80);
        var path = ws.Combine(segment, segment, segment, fileName);
        Assert.True(path.Length >= 260, "fixture path exceeds MAX_PATH");
        return path;
    }

    private static string? FindUnusedDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        for (var candidate = 'Z'; candidate >= 'D'; candidate--)
        {
            if (!used.Contains(candidate))
            {
                return candidate.ToString();
            }
        }

        return null;
    }
}
