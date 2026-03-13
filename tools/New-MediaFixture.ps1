param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "fixture-media"),
    [int]$Count = 10000,
    [int]$FilesPerFolder = 500,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

if ($Count -lt 1) {
    throw "Count 必须大于 0。"
}

if ($FilesPerFolder -lt 1) {
    throw "FilesPerFolder 必须大于 0。"
}

$pngBytes = [Convert]::FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wn14r8AAAAASUVORK5CYII="
)

$keywords = @(
    "travel",
    "family",
    "workshop",
    "archive",
    "summer",
    "winter",
    "portrait",
    "landscape"
)

if ($Clean -and (Test-Path $OutputRoot)) {
    Remove-Item -Recurse -Force $OutputRoot
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

for ($i = 0; $i -lt $Count; $i++) {
    $folderIndex = [int]($i / $FilesPerFolder)
    $folderPath = Join-Path $OutputRoot ("set-{0:D3}" -f $folderIndex)
    New-Item -ItemType Directory -Force -Path $folderPath | Out-Null

    $keyword = $keywords[$i % $keywords.Count]
    $name = "media-{0:D5}-{1}.png" -f $i, $keyword
    $filePath = Join-Path $folderPath $name

    [System.IO.File]::WriteAllBytes($filePath, $pngBytes)
}

Write-Host "已生成 $Count 个测试图片：" $OutputRoot
Write-Host "建议导入这个目录后测试："
Write-Host "1. 首次进入列表是否只秒开前一页"
Write-Host "2. 滚动到末尾时是否平滑继续加载"
Write-Host "3. 搜索 travel / archive 等关键字时是否快速响应"
