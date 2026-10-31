# Reproducibility baseline

## SDK selection

The repository pins the .NET SDK baseline in [`global.json`](../global.json) to
`10.0.301`, which is the SDK used by the current development environment.
`rollForward: latestFeature` keeps the existing "newest installed compatible
SDK" behavior within the .NET 10 feature-band line: a newer 10.0 feature band
may be selected when the baseline is not installed, while .NET 11 (or a
prerelease SDK) is not selected accidentally. This keeps local and CI builds
on the supported .NET 10 toolchain without requiring every machine to retain
one exact patch SDK.

Verify the selected SDK before a build:

```powershell
dotnet --version
dotnet --info
```

The first command should report `10.0.301` on a machine that has the baseline
installed. A later compatible 10.0 feature band is expected when the baseline
is unavailable.

## Restore and build checks

Run these checks from the repository root in PowerShell 7:

```powershell
dotnet restore .\JvJvMediaManager.sln
dotnet build .\JvJvMediaManager.sln
pwsh .\tools\run-smoke-tests.ps1
```

For a focused data check, use:

```powershell
pwsh .\tools\run-smoke-tests.ps1 -Suite db-transaction
```

The WinUI build still uses `tools\Invoke-WindowsAppSdkDotNet.ps1` through
`build.ps1` when Visual Studio MSBuild integration is required:

```powershell
pwsh -NoLogo -NoProfile -File .\build.ps1
```

## NuGet package policy

Package versions remain explicit in each project file. The repository does not
currently enable NuGet lock files or central package management because the
WinUI app and the WinUI-free smoke harness have different target frameworks,
and the app's RID-specific Windows App SDK graph is sensitive to restore
inputs. The explicit `SQLitePCLRaw.bundle_e_sqlite3` `3.0.3` references are a
security pin and must stay aligned between both projects.

Before introducing lock files or `Directory.Packages.props`, first add a
Windows-capable CI restore job and validate a clean self-contained publish.
Once that baseline exists, generate and enforce lock files with
`dotnet restore --locked-mode` in CI as a separate, reviewable change.
