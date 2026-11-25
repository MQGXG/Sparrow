<#
.SYNOPSIS
    下载 FFmpeg（BtbN win64 GPL 构建）到 tools/ffmpeg，供开发与打包使用。

.DESCRIPTION
    仓库不提交 ffmpeg 二进制（约 45 MB）。第一次构建 / 录制前运行一次：

        pwsh -File scripts/fetch-ffmpeg.ps1

    也可以直接启动应用：首次录制发现缺少编码器时，会自动下载到
    %LOCALAPPDATA%\Sparrow\ffmpeg。

.PARAMETER Destination
    输出目录，默认 <repo>/tools/ffmpeg。

.PARAMETER Url
    下载地址，默认 BtbN 的 latest win64 GPL 构建（含 libx264 / nvenc / amf / qsv / mf）。
#>
param(
    [string]$Destination,
    [string]$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $root 'tools\ffmpeg'
}

$ffmpeg = Join-Path $Destination 'ffmpeg.exe'
if (Test-Path -LiteralPath $ffmpeg) {
    Write-Host "ffmpeg 已存在：$Destination"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$zip = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString('N') + ".zip")

Write-Host "下载 FFmpeg（约 120 MB）：$Url"

try {
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -L --fail --retry 3 --progress-bar -o $zip $Url
        if ($LASTEXITCODE -ne 0) {
            throw "curl 下载失败：exit $LASTEXITCODE"
        }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $zip
    }

    Write-Host "解压到：$Destination"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)

    try {
        foreach ($entry in $archive.Entries) {
            $name = [System.IO.Path]::GetFileName($entry.FullName)

            $isBinary = $name -eq 'ffmpeg.exe' -or $name -like '*.dll'
            $isLicense = $name -like 'LICENSE*'

            if (-not $isBinary -and -not $isLicense) {
                continue
            }

            $target = Join-Path $Destination $(if ($isLicense) { 'LICENSE-FFmpeg.txt' } else { $name })
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally {
        $archive.Dispose()
    }

    if (-not (Test-Path -LiteralPath $ffmpeg)) {
        throw '压缩包里没有 ffmpeg.exe。'
    }

    Write-Host "完成：$Destination"
} finally {
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
}
