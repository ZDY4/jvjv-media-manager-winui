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

& dotnet @dotnetArgs
exit $LASTEXITCODE
