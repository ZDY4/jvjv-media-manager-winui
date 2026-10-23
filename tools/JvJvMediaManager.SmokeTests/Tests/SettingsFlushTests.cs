using System.Text.Json;
using JvJvMediaManager.Models;
using JvJvMediaManager.Services;
using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.Utilities;

namespace JvJvMediaManager.SmokeTests.Tests;

internal static class SettingsFlushTests
{
    private const string Suite = "settings-flush";
    private static readonly SemaphoreSlim StorageRootGate = new(1, 1);

    public static void Register(TestRegistry registry)
    {
        registry.Add(Suite, "debounced save eventually persists last change", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await using var service = new SettingsService();
                service.SetLockPassword("hunter2");

                await WaitForCondition(
                    () => File.Exists(settingsPath) && ReadLockPassword(settingsPath) == "hunter2",
                    TimeSpan.FromSeconds(3));

                Assert.Equal("hunter2", ReadLockPassword(settingsPath), "Debounced save persisted the value");
            });
        });

        registry.Add(Suite, "high-frequency updates coalesce to final value", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await using var service = new SettingsService();
                for (var i = 0; i < 50; i++)
                {
                    service.SetMediaBrowserIconSize(80 + i);
                }

                await WaitForCondition(
                    () => File.Exists(settingsPath) && ReadIconSize(settingsPath) == 129,
                    TimeSpan.FromSeconds(3));

                Assert.Equal(129, ReadIconSize(settingsPath), "Final coalesced value persisted");
            });
        });

        // WP-03: flush on shutdown must not lose the last change even if the
        // debounce window has not elapsed.
        registry.Add(Suite, "flush persists last change before debounce elapses", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await using var service = new SettingsService();
                service.SetPlaybackMode("shuffle-immediate-close");

                await service.FlushAsync();

                Assert.True(File.Exists(settingsPath), "Settings file exists after flush");
                Assert.Equal("shuffle-immediate-close", ReadPlaybackMode(settingsPath), "Flush persisted the last change immediately");
            });
        });

        registry.Add(Suite, "updates after dispose are ignored", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                var service = new SettingsService();
                service.SetLockPassword("before-dispose");
                await service.FlushAsync();
                await service.DisposeAsync();

                service.SetLockPassword("after-dispose");
                await Task.Delay(700);

                Assert.Equal("before-dispose", ReadLockPassword(settingsPath), "Post-dispose updates are ignored");
            });
        });

        registry.Add(Suite, "auto advance defaults on and persists explicit opt-out", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await using var service = new SettingsService();

                Assert.True(service.PlaybackAutoAdvanceEnabled, "Auto advance defaults to enabled for a new settings file");

                service.SetPlaybackAutoAdvanceEnabled(false);
                await service.FlushAsync();
                Assert.False(service.PlaybackAutoAdvanceEnabled, "Explicitly disabling auto advance is reflected immediately");
                Assert.False(ReadBooleanProperty(settingsPath, "PlaybackAutoAdvanceEnabled"), "Explicit opt-out is persisted");
            });
        });

        registry.Add(Suite, "failed replacement and promotion retain recoverable settings", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await File.WriteAllTextAsync(settingsPath, "{\"PlaybackMode\":\"old\"}");
                File.SetLastWriteTimeUtc(settingsPath, DateTime.UtcNow.AddMinutes(-1));
                var tempPath = settingsPath + ".tmp";
                using (var lockedFile = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await using (var service = new SettingsService())
                    {
                        service.SetPlaybackMode("recovered");
                        await service.FlushAsync();
                        Assert.Equal("old", ReadPlaybackMode(settingsPath), "Locked destination stays intact");
                        Assert.Equal("recovered", ReadPlaybackMode(tempPath), "Verified snapshot survives failed replacement");
                    }

                    await using var retry = new SettingsService();
                    Assert.Equal("recovered", retry.PlaybackMode, "Startup uses the newer snapshot even while promotion is blocked");
                    Assert.True(File.Exists(tempPath), "Failed startup promotion must retain the recovery file");
                }

                await using var recovered = new SettingsService();
                Assert.Equal("recovered", recovered.PlaybackMode, "Next startup recovers the last change");
                Assert.Equal("recovered", ReadPlaybackMode(settingsPath), "Recovery snapshot is promoted after unlock");
                Assert.False(File.Exists(tempPath), "Successful promotion leaves no temp file");
            });
        });

        registry.Add(Suite, "invalid or older temp settings never replace a valid primary", async () =>
        {
            await WithIsolatedStorageRoot(async settingsPath =>
            {
                await File.WriteAllTextAsync(settingsPath, "{\"PlaybackMode\":\"current\"}");
                var tempPath = settingsPath + ".tmp";
                await File.WriteAllTextAsync(tempPath, "{partial");
                await using (var service = new SettingsService())
                {
                    Assert.Equal("current", service.PlaybackMode, "Partial snapshot is ignored");
                    Assert.False(File.Exists(tempPath), "Partial snapshot is cleaned up");
                }

                await File.WriteAllTextAsync(tempPath, "{\"PlaybackMode\":\"stale\"}");
                File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.AddMinutes(-1));
                await using var reloaded = new SettingsService();
                Assert.Equal("current", reloaded.PlaybackMode, "Older snapshot cannot roll settings back");
                Assert.False(File.Exists(tempPath), "Stale snapshot is cleaned up");
            });
        });
    }

    private static async Task WithIsolatedStorageRoot(Func<string, Task> body)
    {
        await StorageRootGate.WaitAsync();
        var previous = Environment.GetEnvironmentVariable(AppDataPaths.StorageRootEnvironmentVariable);
        using var ws = new TempWorkspace("settings");
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.StorageRootEnvironmentVariable, ws.Root);
            var settingsPath = Path.Combine(ws.Root, "settings.json");
            await body(settingsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.StorageRootEnvironmentVariable, previous);
            StorageRootGate.Release();
        }
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

    private static string ReadLockPassword(string settingsPath)
        => ReadProperty(settingsPath, "LockPassword") ?? string.Empty;

    private static string ReadPlaybackMode(string settingsPath)
        => ReadProperty(settingsPath, "PlaybackMode") ?? string.Empty;

    private static int ReadIconSize(string settingsPath)
    {
        var raw = ReadRawProperty(settingsPath, "MediaBrowserIconSize");
        return raw.HasValue && raw.Value.ValueKind == JsonValueKind.Number ? raw.Value.GetInt32() : -1;
    }

    private static string? ReadProperty(string settingsPath, string property)
    {
        var raw = ReadRawProperty(settingsPath, property);
        return raw is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    }

    private static bool ReadBooleanProperty(string settingsPath, string property)
    {
        var raw = ReadRawProperty(settingsPath, property);
        return raw is { ValueKind: JsonValueKind.True };
    }

    private static JsonElement? ReadRawProperty(string settingsPath, string property)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return doc.RootElement.TryGetProperty(property, out var value) ? value.Clone() : null;
    }
}
