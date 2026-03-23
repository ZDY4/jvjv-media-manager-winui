param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Build', 'Publish')]
    [string]$Action,

    [Parameter(Mandatory = $true)]
    [string]$Target,

    [string]$Configuration = 'Debug',

    [string]$RuntimeIdentifier,

    [string]$Output,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

$ErrorActionPreference = 'Stop'

function Get-VsMsBuildPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe was not found at '$vswhere'."
    }

    $vsJson = & $vswhere -latest -products * -format json
    if (-not $vsJson) {
        throw 'vswhere.exe did not return any Visual Studio installations.'
    }

    $instances = $vsJson | ConvertFrom-Json
    if ($instances -isnot [System.Array]) {
        $instances = @($instances)
    }

    $instance = $instances | Select-Object -First 1
    if (-not $instance) {
        throw 'No Visual Studio installation was found.'
    }

    $installationPath = $instance.installationPath
    if (-not $installationPath) {
        throw 'Visual Studio installation metadata is incomplete.'
    }

    $msbuildPath = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (-not (Test-Path $msbuildPath)) {
        throw "MSBuild.exe was not found under '$installationPath'."
    }

    return $msbuildPath
}

function Get-AppxMsBuildToolsPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe was not found at '$vswhere'."
    }

    $vsJson = & $vswhere -latest -products * -format json
    if (-not $vsJson) {
        throw 'vswhere.exe did not return any Visual Studio installations.'
    }

    $instances = $vsJson | ConvertFrom-Json
    if ($instances -isnot [System.Array]) {
        $instances = @($instances)
    }

    $instance = $instances | Select-Object -First 1
    if (-not $instance) {
        throw 'No Visual Studio installation was found.'
    }

    $installationPath = $instance.installationPath
    $installationVersion = [string]$instance.installationVersion
    if (-not $installationPath -or -not $installationVersion) {
        throw 'Visual Studio installation metadata is incomplete.'
    }

    $majorVersion = ($installationVersion -split '\.')[0]
    $appxToolsPath = Join-Path $installationPath "MSBuild\Microsoft\VisualStudio\v$majorVersion.0\AppxPackage"

    if (-not (Test-Path (Join-Path $appxToolsPath 'Microsoft.Build.AppxPackage.dll'))) {
        throw "AppxPackage tooling was not found under '$appxToolsPath'. Install the WinUI application development workload."
    }

    return [System.IO.Path]::GetFullPath($appxToolsPath) + '\'
}

$appxMsBuildToolsPath = Get-AppxMsBuildToolsPath

$dotnetArgs = @(
    $Action.ToLowerInvariant()
    $Target
    '-c'
    $Configuration
    "-p:AppxMSBuildToolsPath=$appxMsBuildToolsPath"
)

if ($RuntimeIdentifier) {
    $dotnetArgs += @('-r', $RuntimeIdentifier)
}

if ($Output) {
    $dotnetArgs += @('-o', $Output)
}

if ($AdditionalArgs) {
    $dotnetArgs += $AdditionalArgs
}

if ($Action -eq 'Build') {
    $msbuildPath = Get-VsMsBuildPath
    $msbuildArgs = @(
        $Target
        '/restore'
        '/t:Build'
        "/p:Configuration=$Configuration"
        "/p:AppxMSBuildToolsPath=$appxMsBuildToolsPath"
        '/nologo'
    )

    if ($AdditionalArgs) {
        $msbuildArgs += $AdditionalArgs
    }

    & $msbuildPath @msbuildArgs
    if ($LASTEXITCODE -eq 0) {
        exit 0
    }

    Write-Warning "MSBuild exited with code $LASTEXITCODE. Falling back to 'dotnet build'."
}

& dotnet @dotnetArgs
exit $LASTEXITCODE
