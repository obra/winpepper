# Generates a 16x16 placeholder ICO at the path passed as $args[0]. The icon is
# a solid steel-blue square; Plan 6 ships the real artwork.
param([string]$Out = "src\Winpepper.App\Assets\AppIcon.ico")

Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap 16, 16
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 70, 130, 180))  # SteelBlue
$g.Dispose()

# Convert to an .ico — Icon.FromHandle on a HBITMAP is the minimal path.
$hIcon = $bmp.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $fs = [System.IO.File]::Create($Out)
    try { $icon.Save($fs) } finally { $fs.Dispose() }
    Write-Host "Wrote placeholder icon: $Out"
} finally {
    [Winpepper.Native.NativeMethods]::DestroyIcon($hIcon) 2>$null
    $bmp.Dispose()
}
