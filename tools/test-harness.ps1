# Dev check: renders tools\harness.html (Claude-like UI from Claude's design tokens) with
# headless Chrome, shows each render in a plain borderless window (frameless, like Claude's)
# and snapshots that window through the compositor: as is, with a dialog open, as an empty
# new chat, with a browser pane, under an image viewer, as a fullscreen video and in the dark
# theme. Writes artifacts\h-*.png. -Only menu,video runs just those cases.
param([string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\dev\ClaudeBackdrop.exe'), [string[]]$Only)
$ErrorActionPreference = 'Stop'
Add-Type -Namespace H -Name W -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public static IntPtr Find(string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { var s = new System.Text.StringBuilder(256); GetWindowText(h, s, 256);
        if (IsWindowVisible(h) && s.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found; }
'@
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
$url = 'file:///' + ((Join-Path $root 'tools\harness.html') -replace '\\', '/')
$pwsh = (Get-Process -Id $PID).Path

# Shows a PNG at its own pixel size in a borderless window titled "Backdrop harness".
$viewer = @'
param($path)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::SetHighDpiMode('PerMonitorV2') | Out-Null
$img = [Drawing.Image]::FromFile($path)
$form = New-Object Windows.Forms.Form
$form.Text = 'Backdrop harness'; $form.FormBorderStyle = 'None'; $form.ShowInTaskbar = $false
$form.StartPosition = 'Manual'; $form.Location = New-Object Drawing.Point(40, 40)
$form.ClientSize = $img.Size; $form.BackgroundImage = $img
[Windows.Forms.Application]::Run($form)
'@
$viewerPath = Join-Path $root 'artifacts\harness-viewer.ps1'
[IO.File]::WriteAllText($viewerPath, $viewer)

function Shoot([string]$hash, [string]$out, [string[]]$extra = @()) {
    if ($Only -and ($Only -notcontains [IO.Path]::GetFileNameWithoutExtension($out).Substring(2))) { return }
    $page = [IO.Path]::ChangeExtension($out, '.page.png')
    Start-Process $chrome -Wait -WindowStyle Hidden -ArgumentList '--headless=new', '--hide-scrollbars', '--force-device-scale-factor=1',
        "--screenshot=`"$page`"", '--window-size=1500,900', "$url$hash" | Out-Null
    $view = Start-Process $pwsh -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-File', "`"$viewerPath`"", "`"$page`""
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew(); $h = [IntPtr]::Zero
        while ($sw.ElapsedMilliseconds -lt 15000 -and $h -eq [IntPtr]::Zero) { [Threading.Thread]::Sleep(200); $h = [H.W]::Find('Backdrop harness') }
        if ($h -eq [IntPtr]::Zero) { throw 'harness window did not appear' }
        [Threading.Thread]::Sleep(500)
        $p = Start-Process $Exe -ArgumentList (@('--snapshot', "`"$out`"", '--window', ('0x{0:X}' -f $h.ToInt64())) + $extra) -PassThru -Wait
    }
    finally { Stop-Process -Id $view.Id -Force -ErrorAction SilentlyContinue }
    $line = (Get-Content (Join-Path $env:LOCALAPPDATA 'ClaudeBackdrop\log.txt') -Tail 1) -replace '^\S+ snapshot \S+ ', ''
    "{0,-10} {1}" -f [IO.Path]::GetFileNameWithoutExtension($out).Substring(2), $line
}

Shoot '' (Join-Path $root 'artifacts\h-menu.png')
# The engine keeps the page background as its key while a dialog backdrop is open.
Shoot '#modal' (Join-Path $root 'artifacts\h-modal.png') @('--key', 'FCFCFB')
# Arriving at a new chat from a conversation: the page background stays the key.
Shoot '#newchat' (Join-Path $root 'artifacts\h-newchat.png') @('--key', 'FCFCFB')
Shoot '#browser' (Join-Path $root 'artifacts\h-browser.png') @('--key', 'FCFCFB')
# Media fills the window: shown untouched.
Shoot '#lightbox' (Join-Path $root 'artifacts\h-lightbox.png') @('--key', 'FCFCFB')
Shoot '#video' (Join-Path $root 'artifacts\h-video.png') @('--key', 'FCFCFB')
# Switching to the dark theme must replace the background, not be taken for media.
Shoot '#dark' (Join-Path $root 'artifacts\h-dark.png') @('--key', 'FCFCFB')
