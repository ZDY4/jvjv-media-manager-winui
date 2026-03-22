# JvJv Media Manager WinUI

基于 WinUI 3 和 Windows App SDK 的 Windows 媒体管理器。

## 环境要求

- Windows
- PowerShell 7
- Visual Studio Community 2026 或更新版本
- 已安装 WinUI / Windows App SDK 相关工作负载

## 常用命令

### 开发构建

```powershell
pwsh -NoLogo -NoProfile -File .\build.ps1
```

说明：

- `build.ps1` 会优先使用 Visual Studio 自带的 `MSBuild.exe`
- 如果当前机器上的 `MSBuild.exe` 出现 WinUI 项目评估异常，会自动回退到 `dotnet build`
- 脚本会自动补上 `AppxMSBuildToolsPath`，避免部分环境下 `dotnet build` 缺少 `Pri.Tasks.dll`

### 发布

默认发布 `win-x64` 自包含版本到 `artifacts\publish\self-contained`：

```bat
package.bat
```

发布 `win-x64` 框架依赖版本：

```bat
package.bat win-x64 framework-dependent
```

说明：

- framework-dependent 产物依赖目标机器已安装 `.NET 10 Runtime` 和 `Windows App Runtime`
- 默认无参产物为自包含版本，更适合直接拷贝到未预装运行时的机器

输出目录：

- framework-dependent: `artifacts\publish\framework-dependent`
- self-contained: `artifacts\publish\self-contained`

可选运行时：

- `win-x64`
- `win-x86`
- `win-arm64`

## 输出目录

- 构建输出：`src\JvJvMediaManager.WinUI\bin\<Configuration>\net10.0-windows10.0.19041.0`
- 发布输出：`artifacts\publish`

## 文档

- [功能需求](docs/requirements.md)
- [开发计划](docs/development-plan.md)
