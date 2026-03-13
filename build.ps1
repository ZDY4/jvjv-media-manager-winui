param(
    [string]$Configuration = 'Debug',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalArgs
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = Join-Path $root 'tools\Invoke-WindowsAppSdkDotNet.ps1'
$solution = Join-Path $root 'JvJvMediaManager.sln'

& $tool -Action Build -Target $solution -Configuration $Configuration @AdditionalArgs
exit $LASTEXITCODE
