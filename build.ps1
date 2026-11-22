param([string]$OutputPath = 'dist\latest\MonitorScaleTray.exe')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
Add-Type -AssemblyName System.Drawing
if (-not (Test-Path assets\monitor.ico)) {
    $bitmap = New-Object System.Drawing.Bitmap 32,32
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0,95,184))
    $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White),2
    $graphics.FillRectangle($blue,1,4,30,22)
    $graphics.DrawRectangle($white,5,8,22,13)
    $graphics.DrawLine($white,16,22,16,28)
    $graphics.DrawLine($white,10,28,22,28)
    $graphics.DrawLine($white,12,17,20,11)
    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    $stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'assets\monitor.ico'))
    $icon.Save($stream)
    $stream.Dispose(); $icon.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $blue.Dispose(); $white.Dispose()
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath src -Filter *.cs | ForEach-Object { $_.FullName }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$OutputPath" /win32icon:assets\monitor.ico /win32manifest:app.manifest /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Output (Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length)
