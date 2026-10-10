using JvJvMediaManager.SmokeTests.Harness;
using JvJvMediaManager.SmokeTests.Tests;
using JvJvMediaManager.Utilities;

// Isolate all diagnostic/log output into a throwaway directory so smoke runs
// never pollute the real data/ folder.
var storageRoot = Path.Combine(Path.GetTempPath(), "jvjvmm-smoke", "storage-root");
Directory.CreateDirectory(storageRoot);
Environment.SetEnvironmentVariable(AppDataPaths.StorageRootEnvironmentVariable, storageRoot);

var registry = new TestRegistry();

MediaTransactionTests.Register(registry);
ConnectionConfigTests.Register(registry);
MediaPagingContractTests.Register(registry);
MediaPositionContractTests.Register(registry);
KeysetPaginationTests.Register(registry);
KeysetScaleContractTests.Register(registry);
FingerprintCancellationTests.Register(registry);
DiskCacheTrimmerTests.Register(registry);
LogGovernanceTests.Register(registry);
SettingsFlushTests.Register(registry);
PlaybackLeaseTests.Register(registry);
PlayerNavigationWindowTests.Register(registry);

string? suiteFilter = null;
for (var i = 0; i < args.Length; i++)
{
    if ((args[i] == "--suite" || args[i] == "-s") && i + 1 < args.Length)
    {
        suiteFilter = args[i + 1];
        i++;
    }
}

var runner = new TestRunner(registry);
return await runner.RunAsync(suiteFilter);
