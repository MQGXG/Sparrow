<#
  开发启动脚本。

  现在 App 的 csproj 已经有构建期插件引用，`dotnet run --project` 也会构建插件；
  这个脚本仍然保留为一键路径：显式构建整个解决方案后再启动，
  适合首次拉取、切换分支、或需要 Release 配置时使用。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

Write-Host "== 构建解决方案（$Configuration，含所有插件） ==" -ForegroundColor Cyan
dotnet build (Join-Path $root 'Sparrow.slnx') -c $Configuration -v quiet
if ($LASTEXITCODE -ne 0) { throw '构建失败' }

Write-Host '== 启动 Sparrow ==' -ForegroundColor Cyan
$runArgs = @('run', '--project', (Join-Path $root 'src/Sparrow.App/Sparrow.App.csproj'), '-c', $Configuration)
if ($AppArgs) { $runArgs += '--'; $runArgs += $AppArgs }

dotnet @runArgs
