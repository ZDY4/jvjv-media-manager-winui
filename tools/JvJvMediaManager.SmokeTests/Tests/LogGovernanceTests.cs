using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class LogGovernanceTests
{
    private const string Suite = "log-governance";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "log writes produce a sharded trace file", async () =>
        {
            var logDir = AppDataPaths.GetLogsDirectory();
            for (var i = 0; i < 20; i++)
            {
                AppTraceLogger.Log("SmokeTest", $"governance-write-{Guid.NewGuid():N}-{i}");
            }

            AppTraceLogger.FlushNow();
            await WaitForCondition(
                () => Directory.Exists(logDir) && Directory.GetFiles(logDir, "trace-*.log").Length > 0,
                TimeSpan.FromSeconds(3));

            Assert.True(
                Directory.GetFiles(logDir, "trace-*.log").Length > 0,
                "At least one sharded trace file was written");
            Assert.True(AppTraceLogger.LogBytesWritten > 0, "Log bytes written counter advanced");
        });

        registry.Add(Suite, "sampled keys stay bounded under high cardinality", async () =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                AppTraceLogger.LogSampled("SmokeTest", $"key-{i}", "sampled message", TimeSpan.FromMinutes(10));
            }

            // Pruning is asynchronous; give it a moment.
            await WaitForCondition(
                () => AppTraceLogger.SampleKeyCount <= 8192,
                TimeSpan.FromSeconds(3));

            Assert.True(
                AppTraceLogger.SampleKeyCount <= 8192,
                $"Sample key cardinality bounded (count={AppTraceLogger.SampleKeyCount})");
        });

        registry.Add(Suite, "counters are observable", () =>
        {
            Assert.True(AppTraceLogger.PendingQueueLength >= 0, "Pending queue length observable");
            Assert.True(AppTraceLogger.DroppedPendingLines >= 0, "Dropped line counter observable");
            Assert.True(AppTraceLogger.FlushFailures >= 0, "Flush failure counter observable");
        });
    }

    private static async Task WaitForCondition(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }
}
