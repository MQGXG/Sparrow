<#
  Sparrow 发布脚本。

  产物结构（artifacts/publish/win-x64/）：
    Sparrow.App.exe
    plugins/
      sparrow.plugin.json-format/
      sparrow.plugin.dev-base64/
      sparrow.plugin.ocr/          ← 里面 worker/ 是 OCR 子进程
    ...

  为什么插件要单独搬：插件不是 App 的 ProjectReference，
  dotnet publish 不会带上它们。插件由 plugins/Directory.Build.targets
  复制到 App 的 bin 目录，发布时再原样搬过去。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Version = '0.1.0',
    [switch]$SkipOcr,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish/$Runtime"
$pluginBin = Join-Path $root "src/Sparrow.App/bin/$Configuration/net10.0/plugins"

Write-Host "== 1/4 构建（$Configuration） ==" -ForegroundColor Cyan
dotnet build (Join-Path $root 'Sparrow.slnx') -c $Configuration -p:Version=$Version -v quiet
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Host "== 2/4 发布 App（$Runtime，自包含） ==" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src/Sparrow.App/Sparrow.App.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDir -v quiet
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

Write-Host "== 3/4 复制插件 ==" -ForegroundColor Cyan
if (-not (Test-Path $pluginBin)) { throw "找不到插件输出目录：$pluginBin" }

$targetPlugins = Join-Path $publishDir 'plugins'
New-Item -ItemType Directory -Force $targetPlugins | Out-Null

foreach ($plugin in Get-ChildItem $pluginBin -Directory) {
    if ($SkipOcr -and $plugin.Name -eq 'sparrow.plugin.ocr') {
        Write-Host "  跳过 $($plugin.Name)（-SkipOcr）"
        continue
    }

    Copy-Item $plugin.FullName -Destination $targetPlugins -Recurse -Force
    Write-Host "  $($plugin.Name)"
}

$size = (Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("== 4/4 完成：{0:N1} MB → {1} ==" -f ($size / 1MB), $publishDir) -ForegroundColor Green

if (-not $NoZip) {
    $zip = Join-Path $artifacts "Sparrow-$Version-$Runtime.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Write-Host '打包 zip（大文件，需要一会儿）…'
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDir, $zip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host ("zip：{0:N1} MB → {1}" -f ((Get-Item $zip).Length / 1MB), $zip) -ForegroundColor Green
}
