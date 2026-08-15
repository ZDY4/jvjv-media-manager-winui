using System.Diagnostics;
using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class FingerprintCancellationTests
{
    private const string Suite = "fingerprint-cancel";

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "compute produces a stable hash", async () =>
        {
            using var ws = new TempWorkspace("fp-stable");
            var file = ws.Combine("sample.bin");
            await File.WriteAllBytesAsync(file, CreateBuffer(4 * 1024 * 1024));

            var length = new FileInfo(file).Length;
            var first = await MediaContentFingerprint.ComputeAsync(file, length, CancellationToken.None);
            var second = await MediaContentFingerprint.ComputeAsync(file, length, CancellationToken.None);

            Assert.Equal(first, second, "Fingerprint is deterministic");
            Assert.True(first.Length == 64, "SHA-256 hex length is 64");
        });

        registry.Add(Suite, "pre-cancelled token stops immediately", async () =>
        {
            using var ws = new TempWorkspace("fp-precancel");
            var file = ws.Combine("sample.bin");
            await File.WriteAllBytesAsync(file, CreateBuffer(8 * 1024 * 1024));

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => MediaContentFingerprint.ComputeAsync(file, new FileInfo(file).Length, cts.Token),
                "Pre-cancelled fingerprint throws");
        });

        registry.Add(Suite, "cancellation during compute responds quickly", async () =>
        {
            using var ws = new TempWorkspace("fp-midcancel");
            var file = ws.Combine("large.bin");
            // Large enough that a full hash would take measurable time.
            await File.WriteAllBytesAsync(file, CreateBuffer(64 * 1024 * 1024));

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(1));

            var sw = Stopwatch.StartNew();
            try
            {
                await MediaContentFingerprint.ComputeAsync(file, new FileInfo(file).Length, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Cancellation responded within a chunk (elapsed={sw.ElapsedMilliseconds}ms)");
        });
    }

    private static byte[] CreateBuffer(int size)
    {
        var buffer = new byte[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        return buffer;
    }
}
