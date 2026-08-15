# Repository Guidelines

## Project Structure & Module Organization

This repository contains a WinUI/.NET media manager. The solution is `JvJvMediaManager.sln`, with the main app under `src/JvJvMediaManager.WinUI`.

- `Controllers/`, `ViewModels/`, `Services/`, `Data/`, `Models/`, and `Utilities/` hold application logic by responsibility.
- `Views/` contains XAML pages, controls, and their code-behind.
- MainPage is a feature module: its code lives in parallel `Controllers/MainPage/`, `Coordinators/MainPage/`, `Services/MainPage/`, `ViewModels/MainPage/`, and `Views/MainPage/` folders. Put new MainPage workflow code in these module folders rather than the top-level ones.
- `Assets/ClipEditor/` and `tools/ffmpeg/` provide runtime assets copied into builds.
- `docs/` stores architecture notes, refactor plans, and validation checklists. Read `docs/media-library-architecture-optimization-plan.md` and `docs/stability-performance-remediation-plan.md` before large media-library/pipeline changes; `docs/plans/` holds feature design docs.
- `tools/JvJvMediaManager.SmokeTests/` is a console stability/performance smoke-test harness run via `tools/run-smoke-tests.ps1`; `tools/New-MediaFixture.ps1` generates fixture media.
- `data/`, `artifacts/`, `.build-check/`, and local shortcuts are development/runtime outputs and should not be treated as source.

## Build, Test, and Development Commands

Use PowerShell 7 from the repository root.

```powershell
dotnet restore JvJvMediaManager.sln
dotnet build JvJvMediaManager.sln
.\build.ps1
.\run-dev.bat
.\run-portable.bat
.\package.bat
pwsh tools/run-smoke-tests.ps1               # all smoke suites
pwsh tools/run-smoke-tests.ps1 -Suite db-transaction
dotnet list src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj package --include-transitive --vulnerable
```

- `dotnet build` is the baseline verification command.
- `build.ps1` wraps `tools\Invoke-WindowsAppSdkDotNet.ps1` (the Windows App SDK build entry point); `package.bat` produces the portable package.
- `run-dev.bat` and `run-portable.bat` launch local app modes.
- The csproj pins `SQLitePCLRaw.bundle_e_sqlite3` to 3.0.3 to mitigate CVE-2025-6965 (GHSA-2m69-gcr7-jv3q) with a bundled SQLite >= 3.50.3 (see the WP-11 comment in the csproj). Do not downgrade this pin; after touching it, re-validate the portable publish and a clean-machine launch. The vulnerability audit may still flag other transitive advisories.

## Coding Style & Naming Conventions

Use C# with nullable reference types enabled and implicit usings. Follow existing namespace and folder conventions: controllers end in `Controller`, view models end in `ViewModel`, services end in `Service`, and repository classes live in `Data`. Prefer async APIs for I/O and database work, pass `CancellationToken` through long-running operations, and use existing helpers such as `IUiThreadDispatcher`, `ExternalProcessRunner`, `PathHelpers`, and `AppTraceLogger`.

Keep XAML code-behind thin; place workflow logic in controllers, coordinators, services, or view models. Use CommunityToolkit MVVM patterns where already adopted.

## Platform & Architecture Constraints

- WinUI 3 desktop app: `net10.0-windows10.0.19041.0`, x64 only (`win-x64` RID, x64 `PlatformTarget`), unpackaged (`WindowsPackageType=None`, self-contained portable publish). Do not assume packaged-app APIs or other platforms/architectures.
- Two-window playback: `MainPage` and `IndependentMediaWindow` share playback state via `MediaPlayerSessionTransfer` and `ActivePlaybackTarget`; `PlaybackResourceCoordinator` tracks active playback through `IPlaybackActivityLease`s. Background or CPU-heavy work should respect `IsPlaybackActive` or take a lease so it does not degrade playback. A boss key (Ctrl+W / double-ESC) hides the independent window.
- ffmpeg is bundled from `tools/ffmpeg/bin/ffmpeg.exe` and copied conditionally; verify packaging when changing media processing.

## Testing Guidelines

There is no unit test project; the smoke-test harness in `tools/JvJvMediaManager.SmokeTests` (`pwsh tools/run-smoke-tests.ps1`, `-Suite` to select one) covers stability/performance scenarios. Validate changes with `dotnet build JvJvMediaManager.sln`, the smoke tests, targeted manual app checks, and any relevant checklist in `docs/` such as `docs/media-library-runtime-validation-checklist.md`. For data, scanning, playback, or file-watcher changes, test cancellation, large libraries, missing files, and repeated open/close flows; `tools/New-MediaFixture.ps1` generates fixture media.

## Commit & Pull Request Guidelines

Recent history uses concise imperative subjects, often with `feat:`, `fix:`, or `docs:` prefixes, for example `fix: preserve player selection after deleting current media`. Keep commits focused and describe user-visible behavior or risk reduction.

PRs should include a short summary, validation commands run, linked issue or plan document when applicable, and screenshots or short clips for UI changes.

## Security & Configuration Tips

Do not commit local media, generated databases, logs, package artifacts, or machine-specific shortcuts. Treat `data/` as local state. FFmpeg binaries under `tools/ffmpeg/` are copied conditionally; verify packaging when changing media processing.
