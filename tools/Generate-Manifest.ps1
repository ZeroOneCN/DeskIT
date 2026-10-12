<#
.SYNOPSIS
    为 DeskIT 发布目录生成 manifest.json（程序文件哈希清单），供程序启动自检做完整性比对。

.DESCRIPTION
    递归枚举发布目录中的文件（自动排除 manifest.json 自身），逐一计算 SHA-256，
    输出清单格式：{ "version": "1.0.0", "files": [ { "path": "DeskIT.exe", "sha256": "..." } ] }

.PARAMETER PublishDirectory
    发布输出目录（例如 dotnet publish 的 -p:DeskITPortable=true 输出目录）。

.PARAMETER Version
    写入清单的程序版本号，默认 1.0.0。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Generate-Manifest.ps1 -PublishDirectory .\artifacts\publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "发布目录不存在：$PublishDirectory"
}

$root = (Resolve-Path -LiteralPath $PublishDirectory).Path

$files = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Name -ne 'manifest.json' } |
    ForEach-Object {
        $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        [pscustomobject]@{
            path   = $relative
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    } |
    Sort-Object path

$manifest = [pscustomobject]@{
    version = $Version
    files   = @($files)
}

$outputPath = Join-Path $root 'manifest.json'
$json = $manifest | ConvertTo-Json -Depth 4
# 以 UTF-8（无 BOM）写出，避免部分工具解析失败。
[System.IO.File]::WriteAllText($outputPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已生成清单：$outputPath（$($files.Count) 个文件）"