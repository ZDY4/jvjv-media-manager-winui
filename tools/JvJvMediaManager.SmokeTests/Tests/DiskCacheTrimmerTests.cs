using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class DiskCacheTrimmerTests
{
    private const string Suite = "cache-quota";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "trims down to target byte budget by least-recent access", () =>
        {
            using var ws = new TempWorkspace("trim-bytes");
            // 20 files x 1KB = 20KB. Max 12KB, target 8KB.
            var paths = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                var path = ws.Combine($"file-{i:00}.thumb");
                File.WriteAllBytes(path, new byte[1024]);
                var accessTime = DateTime.UtcNow.AddMinutes(-i);
                File.SetLastAccessTimeUtc(path, accessTime);
                paths.Add(path);
            }

            var trimmer = new DiskCacheTrimmer(ws.Root, "test");
            var result = trimmer.Trim(new CacheQuota(
                MaxBytes: 12 * 1024,
                MaxFiles: 0,
                MaxAge: TimeSpan.Zero,
                TargetBytesAfterTrim: 8 * 1024), "*.thumb");

            var remaining = Directory.GetFiles(ws.Root, "*.thumb").Length;
            Assert.True(remaining * 1024 <= 8 * 1024, $"Cache trimmed under target (remaining={remaining})");
            Assert.True(result.DeletedFiles >= 12, $"Deleted enough files (deleted={result.DeletedFiles})");

            // Most-recently-accessed (file-00) should survive; oldest deleted.
            Assert.True(File.Exists(paths[0]), "Most-recently-accessed file retained");
            Assert.False(File.Exists(paths[19]), "Least-recently-accessed file evicted");
        });

        registry.Add(Suite, "evicts by max file count", () =>
        {
            using var ws = new TempWorkspace("trim-files");
            for (var i = 0; i < 30; i++)
            {
                var path = ws.Combine($"file-{i:00}.thumb");
                File.WriteAllBytes(path, new byte[16]);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
            }

            var trimmer = new DiskCacheTrimmer(ws.Root, "test");
            trimmer.Trim(new CacheQuota(
                MaxBytes: 0,
                MaxFiles: 10,
                MaxAge: TimeSpan.Zero,
                TargetBytesAfterTrim: 0), "*.thumb");

            var remaining = Directory.GetFiles(ws.Root, "*.thumb").Length;
            Assert.True(remaining <= 10, $"File count trimmed to budget (remaining={remaining})");
        });

        registry.Add(Suite, "removes files older than max age", () =>
        {
            using var ws = new TempWorkspace("trim-age");
            var fresh = ws.Combine("fresh.thumb");
            var old = ws.Combine("old.thumb");
            File.WriteAllBytes(fresh, new byte[16]);
            File.WriteAllBytes(old, new byte[16]);
            File.SetLastAccessTimeUtc(fresh, DateTime.UtcNow);
            File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddDays(-120));

            var trimmer = new DiskCacheTrimmer(ws.Root, "test");
            trimmer.Trim(new CacheQuota(
                MaxBytes: 0,
                MaxFiles: 0,
                MaxAge: TimeSpan.FromDays(90),
                TargetBytesAfterTrim: 0), "*.thumb");

            Assert.True(File.Exists(fresh), "Fresh file retained");
            Assert.False(File.Exists(old), "Aged-out file removed");
        });

        registry.Add(Suite, "skips temp and partial files", () =>
        {
            using var ws = new TempWorkspace("trim-skip");
            var tmp = ws.Combine("inflight.thumb.abc.tmp");
            var partial = ws.Combine("half.partial");
            File.WriteAllBytes(tmp, new byte[4096]);
            File.WriteAllBytes(partial, new byte[4096]);
            File.SetLastAccessTimeUtc(tmp, DateTime.UtcNow.AddDays(-200));
            File.SetLastAccessTimeUtc(partial, DateTime.UtcNow.AddDays(-200));

            var trimmer = new DiskCacheTrimmer(ws.Root, "test");
            trimmer.Trim(new CacheQuota(
                MaxBytes: 1,
                MaxFiles: 0,
                MaxAge: TimeSpan.FromDays(1),
                TargetBytesAfterTrim: 1), "*");

            Assert.True(File.Exists(tmp), "Temp file skipped");
            Assert.True(File.Exists(partial), "Partial file skipped");
        });
    }
}
