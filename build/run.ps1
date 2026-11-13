<#
  开发启动脚本。

  为什么需要它：
  `dotnet run --project src/Sparrow.App` 只会构建 App 和它直接引用的项目，
  不会构建 plugins/ 下的插件。新拉取仓库时，如果你的 bin 里没有旧插件产物，
  工作区就会一个工具都没有，或者看不到新加的「翻译 / 提示词生成 SVG」。

  这个脚本先构建整个解决方案（含所有插件），再启动 App。
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
