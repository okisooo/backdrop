[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FramesDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
New-Item -ItemType Directory -Path $FramesDirectory,$OutputDirectory -Force | Out-Null
dotnet run --project (Join-Path $PSScriptRoot 'Backdrop.Demo') -c Release -- $FramesDirectory
if ($LASTEXITCODE -ne 0) { throw 'demo rendering failed' }
foreach ($name in @('overview','readability','wallpaper')) {
    $destination = if ($name -eq 'wallpaper') { 'sample-wallpaper.gif' } else { "$name.gif" }
    $filter = if ($name -eq 'wallpaper') {
        'split[a][b];[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer:bayer_scale=3'
    } else {
        'fps=15,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle'
    }
    ffmpeg -hide_banner -loglevel error -y -framerate 15 -i (Join-Path $FramesDirectory "$name\%04d.png") -filter_complex $filter -loop 0 (Join-Path $OutputDirectory $destination)
    if ($LASTEXITCODE -ne 0) { throw "$name GIF encoding failed" }
}
ffmpeg -hide_banner -loglevel error -y -framerate 15 -i (Join-Path $FramesDirectory 'overview\%04d.png') -c:v libx264 -crf 18 -pix_fmt yuv420p -movflags +faststart (Join-Path $OutputDirectory 'backdrop-demo.mp4')
if ($LASTEXITCODE -ne 0) { throw 'video encoding failed' }
Copy-Item -LiteralPath (Join-Path $FramesDirectory 'social-preview.png'),(Join-Path $FramesDirectory 'demo-still.png') -Destination $OutputDirectory -Force
