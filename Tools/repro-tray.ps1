# Repro: right-click the Luma tray icon programmatically and detect a hung UI thread.
# $Exe  - path to Luma.exe to launch
# exit 0 = GREEN (responsive, no hang); exit 10 = RED (hung); exit 20 = routing failure.
param(
    [Parameter(Mandatory = $true)][string]$Exe
)

$ErrorActionPreference = 'Stop'
Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int count);
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@

# Tray callback registered by TrayIconService: 0x8000 + 0x155.
$CallbackMessage = 0x8155
$WmLeftButtonUp = 0x0202
$WmContextMenu  = 0x007B
$WmNull         = 0x0000
$SmtoAbortIfHung = 0x0002

$exePath = (Resolve-Path $Exe).Path
$exeName = [IO.Path]::GetFileNameWithoutExtension($Exe)

Get-Process -Name $exeName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$proc = Start-Process -FilePath $exePath -ArgumentList '--silent' -PassThru
try {
    # Wait for the WPF main window (class HwndWrapper[...]) of the process.
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $hwnd = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline -and $hwnd -eq [IntPtr]::Zero) {
        if ($proc.HasExited) { throw "process exited during startup" }
        $script:procId = $proc.Id
        $script:candidates = @()
        $cb = {
            param($h, $l)
            $pid2 = [UInt32]0
            [Win32.Native]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
            if ($pid2 -eq [UInt32]$script:procId) { $script:candidates += $h }
            return $true
        }
        [Win32.Native]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
        foreach ($h in $script:candidates) {
            $cls = New-Object System.Text.StringBuilder 256
            [Win32.Native]::GetClassName($h, $cls, 256) | Out-Null
            $t = New-Object System.Text.StringBuilder 256
            [Win32.Native]::GetWindowText($h, $t, 256) | Out-Null
            if ($cls.ToString().StartsWith('HwndWrapper[') -and $t.ToString() -eq 'Luma') { $hwnd = $h; break }
        }
        if ($hwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "main window not found" }
    $title = New-Object System.Text.StringBuilder 256
    [Win32.Native]::GetWindowText($hwnd, $title, 256) | Out-Null
    Write-Output "window: 0x$($hwnd.ToString('X')) class-title=$($title.ToString())"

    # Baseline: UI thread answers WM_NULL within 2s.
    $result = [IntPtr]::Zero
    $ok = [Win32.Native]::SendMessageTimeout($hwnd, $WmNull, [IntPtr]::Zero, [IntPtr]::Zero, $SmtoAbortIfHung, 2000, [ref]$result)
    if (-not $ok) { throw "UI thread already hung BEFORE right-click (baseline)" }

    # Step 1: prove callback routing with the LEFT-click notification -> window must show.
    [Win32.Native]::PostMessage($hwnd, $CallbackMessage, [IntPtr]::Zero, [IntPtr]$WmLeftButtonUp) | Out-Null
    $shown = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 150
        if ([Win32.Native]::IsWindowVisible($hwnd)) { $shown = $true; break }
    }
    if (-not $shown) {
        Write-Output "ROUTING-FAIL: left-click tray callback did not show the window"
        exit 20
    }
    Write-Output "routing: left-click callback OK (window shown)"

    # Baseline again after show.
    $result = [IntPtr]::Zero
    $ok = [Win32.Native]::SendMessageTimeout($hwnd, $WmNull, [IntPtr]::Zero, [IntPtr]::Zero, $SmtoAbortIfHung, 2000, [ref]$result)
    if (-not $ok) { throw "UI thread hung after left-click" }

    # Step 2: simulate the tray RIGHT-click callback (LOWORD(lParam)=WM_CONTEXTMENU).
    [Win32.Native]::PostMessage($hwnd, $CallbackMessage, [IntPtr]::Zero, [IntPtr]$WmContextMenu) | Out-Null

    $anyHang = $false
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 400
        $result = [IntPtr]::Zero
        $ok = [Win32.Native]::SendMessageTimeout($hwnd, $WmNull, [IntPtr]::Zero, [IntPtr]::Zero, $SmtoAbortIfHung, 1500, [ref]$result)
        if (-not $ok) { $anyHang = $true; break }
    }

    if ($anyHang) {
        Write-Output "RED: UI thread HUNG after tray right-click"
        exit 10
    }
    Write-Output "GREEN: UI thread responsive after tray right-click"
    exit 0
}
finally {
    Get-Process -Id $proc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
}
