$msiPath = "D:\Programs\AutoClawLinda\wireguard.msi"
$outDir  = "D:\Programs\AutoClawLinda"

# Open MSI database
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msiPath, 0)

# Query Binary table for wintun.dll entries
$view = $db.OpenView("SELECT `Name`, `Data` FROM `Binary` WHERE `Name` LIKE '%wintun%'")
$view.Execute()
$record = $view.Fetch()

while ($record -ne $null) {
    $name = $record.StringData(1)
    Write-Host "Found: $name"
    
    # Get binary stream
    $stream = $record.get_Stream(2)
    $outPath = Join-Path $outDir $name
    
    $fs = [System.IO.File]::OpenWrite($outPath)
    $buffer = New-Object byte[] 8192
    while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $fs.Write($buffer, 0, $read)
    }
    $fs.Close()
    $stream.Close()
    
    $size = (Get-Item $outPath).Length
    Write-Host "  Extracted: $outPath ($size bytes)"
    
    $record = $view.Fetch()
}

$view.Close()
Write-Host "Done."
