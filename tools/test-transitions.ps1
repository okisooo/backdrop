# Dev check: minimizes and restores Claude, sampling the overlay's visibility and whether
# it sits directly above Claude. Run with the app running and "Trace": true in config.json
# to correlate with the engine's own transition log.
$ErrorActionPreference = 'Stop'
Add-Type -Namespace T -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
'@

$claude = (Get-Process claude | Where-Object { $_.MainWindowTitle -like 'Claude*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1).MainWindowHandle
$overlay = [IntPtr]::Zero
$h = [IntPtr]::Zero
do {
    $h = [T.U]::FindWindowEx([IntPtr]::Zero, $h, [NullString]::Value, 'Claude Backdrop')
    if ($h -ne [IntPtr]::Zero -and (([T.U]::GetWindowLongPtr($h, -20).ToInt64() -band 0x80000) -ne 0)) { $overlay = $h }
} while ($h -ne [IntPtr]::Zero -and $overlay -eq [IntPtr]::Zero)
if ($claude -eq [IntPtr]::Zero -or $overlay -eq [IntPtr]::Zero) { throw 'Claude or the overlay window was not found' }

function State { "overlay visible=$([T.U]::IsWindowVisible($overlay)), directly above Claude=$([T.U]::GetWindow($claude, 3) -eq $overlay)" }

$sw = [Diagnostics.Stopwatch]::StartNew()
$last = ''
function Sample([long]$until, [int]$step) {
    while ($sw.ElapsedMilliseconds -lt $until) {
        $s = State
        if ($s -ne $script:last) { '{0,5} ms  {1}' -f $sw.ElapsedMilliseconds, $s; $script:last = $s }
        [Threading.Thread]::Sleep($step)
    }
}

Sample 200 20
'{0,5} ms  -- minimize' -f $sw.ElapsedMilliseconds
[T.U]::ShowWindow($claude, 6) | Out-Null
Sample 1700 20
'{0,5} ms  -- restore' -f $sw.ElapsedMilliseconds
[T.U]::ShowWindow($claude, 9) | Out-Null
Sample 3000 10

'{0,5} ms  -- another app takes focus and covers Claude' -f $sw.ElapsedMilliseconds
$cover = Start-Process conhost.exe -ArgumentList 'cmd.exe /k title cover' -WindowStyle Maximized -PassThru
Sample 4300 20
'{0,5} ms  -- that app closes, Claude is active again' -f $sw.ElapsedMilliseconds
Stop-Process -Id $cover.Id -Force
Sample 5500 10