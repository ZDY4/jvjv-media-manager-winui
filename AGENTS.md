# Repository Guidelines

## Project Structure & Module Organization

This repository contains a WinUI/.NET media manager. The solution is `JvJvMediaManager.sln`, with the main app under `src/JvJvMediaManager.WinUI`.

- `Controllers/`, `ViewModels/`, `Services/`, `Data/`, `Models/`, and `Utilities/` hold application logic by responsibility.
- `Views/` contains XAML pages, controls, and their code-behind.
- `Assets/ClipEditor/` and `tools/ffmpeg/` provide runtime assets copied into builds.
- `docs/` stores architecture notes, refactor plans, and validation checklists.
- `data/`, `artifacts/`, `.build-check/`, and local shortcuts are development/runtime outputs and should not be treated as source.

## Build, Test, and Development Commands

Use PowerShell 7 from the repository root.

```powershell
dotnet restore JvJvMediaManager.sln
dotnet build JvJvMediaManager.sln
.\build.ps1
.\run-dev.bat
.\run-portable.bat
dotnet list src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj package --include-transitive --vulnerable
```

- `dotnet build` is the baseline verification command.
- `build.ps1` performs the repository build flow.
- `run-dev.bat` and `run-portable.bat` launch local app modes.
- The vulnerability audit is expected to report known transitive SQLitePCLRaw advisories until upstream dependencies change.

## Coding Style & Naming Conventions

Use C# with nullable reference types enabled and implicit usings. Follow existing namespace and folder conventions: controllers end in `Controller`, view models end in `ViewModel`, services end in `Service`, and repository classes live in `Data`. Prefer async APIs for I/O and database work, pass `CancellationToken` through long-running operations, and use existing helpers such as `IUiThreadDispatcher`, `ExternalProcessRunner`, `PathHelpers`, and `AppTraceLogger`.

Keep XAML code-behind thin; place workflow logic in controllers, coordinators, services, or view models. Use CommunityToolkit MVVM patterns where already adopted.

## Testing Guidelines

There is no dedicated test project yet. Validate changes with `dotnet build JvJvMediaManager.sln`, targeted manual app checks, and any relevant checklist in `docs/`. For data, scanning, playback, or file-watcher changes, test cancellation, large libraries, missing files, and repeated open/close flows.

## Commit & Pull Request Guidelines

Recent history uses concise imperative subjects, often with `feat:`, `fix:`, or `docs:` prefixes, for example `fix: preserve player selection after deleting current media`. Keep commits focused and describe user-visible behavior or risk reduction.

PRs should include a short summary, validation commands run, linked issue or plan document when applicable, and screenshots or short clips for UI changes.

## Security & Configuration Tips

Do not commit local media, generated databases, logs, package artifacts, or machine-specific shortcuts. Treat `data/` as local state. FFmpeg binaries under `tools/ffmpeg/` are copied conditionally; verify packaging when changing media processing.
