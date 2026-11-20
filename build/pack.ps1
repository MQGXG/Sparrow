<#
  Sparrow 打包脚本：发布（自包含）→ Velopack 生成安装包 / 增量包。

  产物（artifacts/releases/）：
    Sparrow-win-Setup.exe      ← 安装器（用户下载这个）
    Sparrow-win-Portable.zip   ← 免安装版
    Sparrow-<版本>-full.nupkg  ← 增量更新的基线
    RELEASES                   ← 更新源索引（自托管时一起上传）

  前置：dotnet tool install -g vpk
  说明：安装版才走 Velopack 的更新钩子；直接 dotnet run 不受影响。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [switch]$SkipOcr,
    [switch]$FrameworkDependent,
    # 同一个版本重复打包时 Velopack 会拒绝；本地验证用 -Clean 清掉旧产物
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root 'artifacts/publish/win-x64'
$releasesDir = Join-Path $root 'artifacts/releases'

if ($Clean -and (Test-Path $releasesDir)) {
    Remove-Item $releasesDir -Recurse -Force
    Write-Host "  已清空旧发布产物（-Clean）"
}

Write-Host '== 1/2 发布 ==' -ForegroundColor Cyan
$args = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'publish.ps1'),
    '-Version', $Version, '-NoZip')
if ($SkipOcr) { $args += '-SkipOcr' }
& pwsh @args
if ($LASTEXITCODE -ne 0) { throw '发布失败' }

Write-Host '== 2/2 打包（Velopack） ==' -ForegroundColor Cyan
$vpk = Join-Path $env:USERPROFILE '.dotnet/tools/vpk.exe'
if (-not (Test-Path $vpk)) { throw '找不到 vpk，请先运行：dotnet tool install -g vpk' }

New-Item -ItemType Directory -Force $releasesDir | Out-Null

& $vpk pack `
    --packId Sparrow `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe Sparrow.App.exe `
    --packTitle 'Sparrow' `
    --packAuthors 'Sparrow' `
    --outputDir $releasesDir

if ($LASTEXITCODE -ne 0) { throw 'Velopack 打包失败' }

Write-Host ''
Get-ChildItem $releasesDir -File |
    Sort-Object Length -Descending |
    ForEach-Object { '{0,10:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name } |
    Write-Host
