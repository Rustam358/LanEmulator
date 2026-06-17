$src = Join-Path $PSScriptRoot 'full-report.txt'
$desktop = [Environment]::GetFolderPath('Desktop')
$dst = Join-Path $desktop 'wintun-step1-2-report.txt'
Copy-Item $src $dst -Force
Write-Host "Copied: $dst"
Write-Host "Size: $((Get-Item $dst).Length) bytes"
