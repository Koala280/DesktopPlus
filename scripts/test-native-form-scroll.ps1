param([string]$Executable = "")
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $repoRoot 'target\debug\DesktopPlus.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$runRoot = Join-Path $repoRoot ('artifacts\form-scroll-tests\' + [Guid]::NewGuid().ToString('N'))
$files = Join-Path $runRoot 'Downloads'
New-Item -ItemType Directory -Path $files -Force | Out-Null
$settingsFile = Join-Path $runRoot 'settings.json'
@{
    Language = 'de'; CloseBehavior = 'Exit'; StartWithWindows = $false
    Windows = @(@{
        PanelTitle = 'Scroll fixture'; FolderPath = $files; DefaultFolderPath = $files
        Left = 80; Top = 80; Width = 620; Height = 500
        Tabs = @(@{ TabName = 'Downloads'; FolderPath = $files; DefaultFolderPath = $files })
    })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsFile -Encoding UTF8
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class FormScrollTest {
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint="SendMessageW")] public static extern IntPtr Send(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint="PostMessageW")] public static extern bool Post(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public static IntPtr Find(int process, string caption) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, data) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process) {
                var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
                if (text.ToString() == caption) { result = window; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static void Focus(IntPtr window) {
        uint process;
        uint target = GetWindowThreadProcessId(window, out process), current = GetCurrentThreadId();
        bool attached = target != current && AttachThreadInput(current, target, true);
        try { SetFocus(window); }
        finally { if (attached) AttachThreadInput(current, target, false); }
    }
    public static void Expose(IntPtr window) {
        uint process;
        uint target = GetWindowThreadProcessId(GetForegroundWindow(), out process), current = GetCurrentThreadId();
        bool attached = target != current && AttachThreadInput(current, target, true);
        try { SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x43); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, target, false); }
    }
}
'@
function Wait-Until([scriptblock]$Condition, [string]$Description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($timer.ElapsedMilliseconds -gt 5000) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 50
    }
}
function Save-Screen([IntPtr]$Window, [string]$Name) {
    [void][FormScrollTest]::DwmFlush()
    $rect = New-Object FormScrollTest+Rect
    [void][FormScrollTest]::GetWindowRect($Window, [ref]$rect)
    $bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $path = Join-Path $runRoot $Name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Assert-Paint([string]$Name) {
    Start-Sleep -Milliseconds 240
    $before = Save-Screen $body "$Name-screen.png"
    # Observe the actual screen before forcing a repaint. PrintWindow alone
    # would repair the stale text/background fragments and conceal this bug.
    [void][FormScrollTest]::RedrawWindow($body, [IntPtr]::Zero, [IntPtr]::Zero, 0x581)
    Start-Sleep -Milliseconds 60
    $after = Save-Screen $body "$Name-repaint.png"
    if ($before -ne $after) { throw "Settings retain stale pixels ($Name); captures: $runRoot" }
}
$cursor = New-Object FormScrollTest+Point
[void][FormScrollTest]::GetCursorPos([ref]$cursor)
# The app's test mode gives this fixture its own instance mutex and disables
# startup/hotkey integration. Only the panel settings area is exercised here.
$reportFile = Join-Path $runRoot 'report.json'
$process = Start-Process -FilePath $Executable -ArgumentList @('--settings', ('"' + $settingsFile + '"'), '--smoke-test', ('"' + $reportFile + '"')) -WindowStyle Hidden -PassThru
try {
    Wait-Until { [FormScrollTest]::Find($process.Id, 'Scroll fixture') -ne [IntPtr]::Zero } 'fixture panel'
    $panel = [FormScrollTest]::Find($process.Id, 'Scroll fixture')
    [void][FormScrollTest]::Post($panel, 0x111, [IntPtr]218, [IntPtr]::Zero)
    Wait-Until {
        $dialog = [FormScrollTest]::Find($process.Id, 'Panel-Einstellungen')
        [FormScrollTest]::IsWindowVisible([FormScrollTest]::GetDlgItem([FormScrollTest]::GetDlgItem($dialog, 801), 1000))
    } 'panel settings'
    $dialog = [FormScrollTest]::Find($process.Id, 'Panel-Einstellungen')
    $body = [FormScrollTest]::GetDlgItem($dialog, 801)
    [FormScrollTest]::Expose($dialog)
    # Keep the caret and pointer animations outside the captured form body.
    [FormScrollTest]::Focus([FormScrollTest]::GetDlgItem($dialog, 700))
    $rect = New-Object FormScrollTest+Rect
    [void][FormScrollTest]::GetWindowRect($body, [ref]$rect)
    [void][FormScrollTest]::SetCursorPos(($rect.Left - 30), ($rect.Top - 30))
    # Small movements expose overlaps between old and new control positions.
    # Large movements bring controls back from both clipped viewport edges.
    $step = 0
    foreach ($offset in @(0, 32, 64, 96, 24, 0, 10000, 48, 0)) {
        [void][FormScrollTest]::Send($body, 0x8047, [IntPtr]$offset, [IntPtr]::Zero)
        Assert-Paint "scroll-$step-$offset"
        $step++
    }
    foreach ($delta in @(-120, -120, 120, 120)) {
        [void][FormScrollTest]::Send($body, 0x20A, [IntPtr]($delta -shl 16), [IntPtr]::Zero)
        Assert-Paint "wheel-$step"
        $step++
    }
    # A page without a scrollbar changes the field width when returning.
    [void][FormScrollTest]::Send($dialog, 0x111, [IntPtr]701, [IntPtr]::Zero)
    [void][FormScrollTest]::Send($dialog, 0x111, [IntPtr]700, [IntPtr]::Zero)
    Assert-Paint 'return-to-general'
    [void](Save-Screen $dialog 'panel-settings.png')
    Write-Host "Panel settings scroll painting passed: $runRoot"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
    [void][FormScrollTest]::SetCursorPos($cursor.X, $cursor.Y)
}
