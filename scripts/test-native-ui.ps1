param(
    [string]$Executable = "",
    [string]$Language = "en",
    [switch]$Capture
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $repoRoot "target\release\DesktopPlus.exe" }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$runRoot = Join-Path $repoRoot ("artifacts\native-ui-tests\" + [Guid]::NewGuid().ToString("N"))
$files = Join-Path $runRoot "files"
$nested = Join-Path $files "Nested"
$empty = Join-Path $runRoot "Empty"
New-Item -ItemType Directory -Force -Path $nested, $empty | Out-Null
[IO.File]::WriteAllText((Join-Path $files "Report.txt"), "A report")
[IO.File]::WriteAllText((Join-Path $files "Notes.md"), "Notes")
[IO.File]::WriteAllText((Join-Path $nested "DeepSearchTarget.txt"), "Nested search result")
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bottomPanelTop = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Bottom - 48
$bitmap = New-Object Drawing.Bitmap 320, 160
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([Drawing.Color]::SteelBlue)
$bitmap.Save((Join-Path $files "Photo.png"), [Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

$settingsFile = Join-Path $runRoot "settings.json"
$reportFile = Join-Path $runRoot "report.json"
$shortcutPath = Join-Path $runRoot "Editor.lnk"
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $env:WINDIR "System32\notepad.exe"
$shortcut.Save()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell)
$settings = @{
    Language = $Language; CloseBehavior = "Exit"; StartWithWindows = $false
    Appearance = @{ BackgroundColor = '#242833' }
    Presets = @(@{ Name = 'Smoke forest'; Settings = @{ BackgroundColor = '#183029'; HeaderColor = '#213B33'; AccentColor = '#86DAB9'; TextColor = '#EFFAF5'; FontFamily = 'Segoe UI'; ItemFontSize = 14 } })
    Windows = @(
        @{ PanelTitle = "Smoke folder"; FolderPath = $files; DefaultFolderPath = $files; Left = 40; Top = 80; Width = 620; Height = 500; Tabs = @(
            @{ TabName = "Dateien"; FolderPath = $files; DefaultFolderPath = $files; ViewMode = "details"; ShowMetadataDimensions = $true },
            @{ TabName = "Projekte"; FolderPath = $nested; DefaultFolderPath = $nested; ViewMode = "details"; ExpandOnHover = $true }
        ) },
        @{ PanelTitle = "Smoke photos"; FolderPath = $files; Left = 680; Top = 80; Width = 480; Height = 450; ViewMode = "photos" },
        @{ PanelTitle = "Smoke pinned"; PanelType = "List"; PinnedItems = @((Join-Path $files "Report.txt"), $shortcutPath); ViewMode = "icons"; Left = 40; Top = 610; Width = 620; Height = 280 },
        @{ PanelTitle = "Smoke collapsed"; FolderPath = $empty; Left = 680; Top = $bottomPanelTop; Width = 249; Height = 48; ExpandedHeight = 300; IsCollapsed = $true }
    )
}
$settings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $settingsFile -Encoding UTF8
if (-not ("DesktopPlusNativeSmoke" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public static class DesktopPlusNativeSmoke {
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr Send(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr SetText(IntPtr window, uint message, IntPtr w, string text);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr window);
    public static void Focus(IntPtr window) {
        uint process;
        uint target = GetWindowThreadProcessId(window, out process);
        uint current = GetCurrentThreadId();
        bool attached = target != current && AttachThreadInput(current, target, true);
        try { SetFocus(window); }
        finally { if (attached) AttachThreadInput(current, target, false); }
    }
    public static void Expose(IntPtr window) {
        uint process;
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out process);
        uint current = GetCurrentThreadId();
        bool attached = foreground != current && AttachThreadInput(current, foreground, true);
        try { SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x43); SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x43); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
    public static IntPtr Popup(int process) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, data) => { uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process && IsWindowVisible(window)) { var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity); if (cls.ToString() == "#32768") { result = window; return false; } }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public static Dictionary<string, IntPtr> Windows(int process) {
        var result = new Dictionary<string, IntPtr>();
        EnumWindows((window, data) => { uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); if (text.Length > 0) result[text.ToString()] = window; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@
}
function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$TimeoutMs = 3000) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($timer.ElapsedMilliseconds -gt $TimeoutMs) { throw "Timed out: $Description" }
        Start-Sleep -Milliseconds 50
    }
}
function Rows([IntPtr]$Window) {
    $list = [DesktopPlusNativeSmoke]::GetDlgItem($Window, 100)
    return [DesktopPlusNativeSmoke]::Send($list, 0x1004, [IntPtr]::Zero, [IntPtr]::Zero).ToInt32()
}
function Height([IntPtr]$Window) { $r=New-Object DesktopPlusNativeSmoke+Rect;[void][DesktopPlusNativeSmoke]::GetWindowRect($Window,[ref]$r);return $r.Bottom-$r.Top }
function Panel-Settings([IntPtr]$Panel) {
    [void][DesktopPlusNativeSmoke]::PostMessage($Panel,0x111,[IntPtr]218,[IntPtr]::Zero)
    $caption=if ($Language -eq 'de') { 'Panel-Einstellungen' } else { 'Panel settings' }
    Wait-Until {
        $dialogs = [DesktopPlusNativeSmoke]::Windows($process.Id)
        if (-not $dialogs.ContainsKey($caption)) { return $false }
        $body = [DesktopPlusNativeSmoke]::GetDlgItem($dialogs[$caption],801)
        [DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($body,1000))
    } 'panel settings dialog controls'
    return ([DesktopPlusNativeSmoke]::Windows($process.Id))[$caption]
}
function Screenshot([IntPtr]$Window, [string]$Name) {
    $rect = New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($Window, [ref]$rect)
    $bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { [void][DesktopPlusNativeSmoke]::PrintWindow($Window, $dc, 2) } finally { $graphics.ReleaseHdc($dc) }
    $bitmap.Save((Join-Path $runRoot $Name), [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}
function Assert-DarkFrame([IntPtr]$Window, [string]$Name, [bool]$Active = $true) {
    # Read the on-screen surface: PrintWindow repaints the client and can conceal a stale native frame.
    if (-not [DesktopPlusNativeSmoke]::SetWindowPos($Window, [IntPtr](-1), 0, 0, 0, 0, 0x43)) { throw "Cannot expose test panel: Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
    [void][DesktopPlusNativeSmoke]::SetForegroundWindow($Window)
    [DesktopPlusNativeSmoke]::Expose($Window)
    Start-Sleep -Milliseconds 100
    [void][DesktopPlusNativeSmoke]::Send($Window, 0x86, [IntPtr]([int]$Active), [IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::DwmFlush()
    $rect = New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($Window, [ref]$rect)
    $bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $center = $bitmap.GetPixel([int]($bitmap.Width / 2), [int]($bitmap.Height / 2))
        if ($center.R -ne 36 -or $center.G -ne 40 -or $center.B -ne 51) {
            $bitmap.Save((Join-Path $runRoot $Name), [Drawing.Imaging.ImageFormat]::Png)
            throw "Panel surface was not visible during frame capture: $($center.ToArgb().ToString('X8')); visible=$([DesktopPlusNativeSmoke]::IsWindowVisible($Window)); style=$([DesktopPlusNativeSmoke]::GetWindowLongPtr($Window, -20)); foreground=$([DesktopPlusNativeSmoke]::GetForegroundWindow()); expected=$Window"
        }
        $bright = 0
        foreach ($y in 60..($bitmap.Height - 20)) {
            foreach ($x in @(2, 3, 4, ($bitmap.Width - 3), ($bitmap.Width - 4), ($bitmap.Width - 5))) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.R -gt 220 -and $pixel.G -gt 220 -and $pixel.B -gt 220) { $bright++ }
            }
        }
        if ($Capture -or $bright -gt 20) { $bitmap.Save((Join-Path $runRoot $Name), [Drawing.Imaging.ImageFormat]::Png) }
        if ($bright -gt 20) { throw "White native frame after activation/context menu: $bright bright edge pixels ($Name)" }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Assert-SettingsNavigation([IntPtr]$Window) {
    # PrintWindow repaints controls and would hide the stale-selection regression.
    # Sample the actual displayed buttons after moving the pointer away.
    $cursor=New-Object DesktopPlusNativeSmoke+Point
    [void][DesktopPlusNativeSmoke]::GetCursorPos([ref]$cursor)
    [DesktopPlusNativeSmoke]::Expose($Window)
    $rect=New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($Window,[ref]$rect)
    function Navigation-Colors([int]$Inset=12) {
        [void][DesktopPlusNativeSmoke]::DwmFlush()
        $bitmap=New-Object Drawing.Bitmap ($rect.Right-$rect.Left),($rect.Bottom-$rect.Top)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
            $bitmap.Save((Join-Path $runRoot 'settings-navigation-screen.png'),[Drawing.Imaging.ImageFormat]::Png)
            foreach ($index in 0..4) {
                $button=New-Object DesktopPlusNativeSmoke+Rect
                [void][DesktopPlusNativeSmoke]::GetWindowRect([DesktopPlusNativeSmoke]::GetDlgItem($Window,(700+$index)),[ref]$button)
                $bitmap.GetPixel($button.Left-$rect.Left+$Inset,[int](($button.Top+$button.Bottom)/2)-$rect.Top).ToArgb()
            }
        } finally {$graphics.Dispose();$bitmap.Dispose()}
    }
    try {
        [void][DesktopPlusNativeSmoke]::SetCursorPos(($rect.Right-40),($rect.Top+75))
        Start-Sleep -Milliseconds 100
        $baseline=@(Navigation-Colors)
        if ($baseline[0] -eq $baseline[4]) {throw 'Selected navigation page is not visibly distinguished'}
        $selected=$baseline[0];$normal=$baseline[4]
        foreach ($page in @(2,4,1,3,0)) {
            $button=[DesktopPlusNativeSmoke]::GetDlgItem($Window,(700+$page))
            [void][DesktopPlusNativeSmoke]::Send($button,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
            Start-Sleep -Milliseconds 210
            $colors=@(Navigation-Colors)
            foreach ($index in 0..4) {
                $expected=if ($index -eq $page) {$selected} else {$normal}
                if ($colors[$index] -ne $expected) {throw "Navigation page $index retains an incorrect marking after selecting page $page"}
            }
        }
        foreach ($page in @(1,3,2,4,0)) {
            [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($Window,(700+$page)),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
            Start-Sleep -Milliseconds 10
        }
        Wait-Until {
            $colors=@(Navigation-Colors)
            $colors[0] -eq $selected -and @($colors[1..4] | Where-Object {$_ -ne $normal}).Count -eq 0
        } 'settling rapid navigation transitions'
        # Hovering another category must clear again without changing the page.
        $hover=[DesktopPlusNativeSmoke]::GetDlgItem($Window,702)
        $button=New-Object DesktopPlusNativeSmoke+Rect
        [void][DesktopPlusNativeSmoke]::GetWindowRect($hover,[ref]$button)
        Wait-Until {
            [void][DesktopPlusNativeSmoke]::SetCursorPos(($button.Left+20),($button.Top+20))
            [void][DesktopPlusNativeSmoke]::Send($hover,0x200,[IntPtr]::Zero,[IntPtr]0x00140014)
            Start-Sleep -Milliseconds 40
            $colors=@(Navigation-Colors)
            $colors[2] -ne $normal -and $colors[0] -eq $selected
        } 'navigation hover while retaining the active page'
        [void][DesktopPlusNativeSmoke]::SetCursorPos(($rect.Right-40),($rect.Top+75))
        Wait-Until { $colors=@(Navigation-Colors); $colors[2] -eq $normal -and $colors[0] -eq $selected } 'clearing navigation hover'
        # Windows hides focus cues for mouse input and reveals them for keyboard
        # navigation. The active category stays selected in both cases.
        [DesktopPlusNativeSmoke]::Focus([DesktopPlusNativeSmoke]::GetDlgItem($Window,700))
        [void][DesktopPlusNativeSmoke]::Send($Window,0x127,[IntPtr]0x00010001,[IntPtr]::Zero)
        [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($Window,700),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
        Start-Sleep -Milliseconds 210
        $mouseBorders=@(Navigation-Colors 0)
        if ($mouseBorders[0] -ne $normal) {throw 'Mouse navigation leaves a keyboard focus outline'}
        [void][DesktopPlusNativeSmoke]::Send($Window,0x127,[IntPtr]0x00010002,[IntPtr]::Zero)
        Start-Sleep -Milliseconds 210
        $keyboardBorders=@(Navigation-Colors 0)
        if ($keyboardBorders[0] -eq $normal) {
            $control=[DesktopPlusNativeSmoke]::GetDlgItem($Window,700)
            throw "Keyboard navigation lost its focus outline: button=$([DesktopPlusNativeSmoke]::Send($control,0xF2,[IntPtr]::Zero,[IntPtr]::Zero)); UI=$([DesktopPlusNativeSmoke]::Send($control,0x129,[IntPtr]::Zero,[IntPtr]::Zero)); formUI=$([DesktopPlusNativeSmoke]::Send($Window,0x129,[IntPtr]::Zero,[IntPtr]::Zero))"
        }
    } finally {
        [void][DesktopPlusNativeSmoke]::SetWindowPos($Window,[IntPtr](-2),0,0,0,0,0x13)
        [void][DesktopPlusNativeSmoke]::SetCursorPos($cursor.X,$cursor.Y)
    }
}
function Assert-SearchPlaceholder([IntPtr]$Window) {
    $search = [DesktopPlusNativeSmoke]::GetDlgItem($Window, 102)
    $list = [DesktopPlusNativeSmoke]::GetDlgItem($Window, 100)
    $cursor = New-Object DesktopPlusNativeSmoke+Point
    [void][DesktopPlusNativeSmoke]::GetCursorPos([ref]$cursor)
    [DesktopPlusNativeSmoke]::Expose($Window)
    $rect = New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($search, [ref]$rect)
    function Assert-Hint([bool]$Visible, [string]$Name) {
        Start-Sleep -Milliseconds 100
        [void][DesktopPlusNativeSmoke]::DwmFlush()
        $bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            # Observe the existing pixels: forced painting would conceal a stale hint.
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
            $background = $bitmap.GetPixel(($bitmap.Width - 1), 0)
            $ink = 0
            # Exclude the insertion caret at the left edge of the empty edit.
            foreach ($x in 5..($bitmap.Width - 2)) {
                foreach ($y in 1..($bitmap.Height - 2)) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.R -gt $background.R + 32 -or $pixel.G -gt $background.G + 32 -or $pixel.B -gt $background.B + 32) { $ink++ }
                }
            }
            if ($Capture -or (($ink -gt 10) -ne $Visible)) {
                $bitmap.Save((Join-Path $runRoot ("search-" + $Name + ".png")), [Drawing.Imaging.ImageFormat]::Png)
            }
            if (($ink -gt 10) -ne $Visible) { throw "Search placeholder visibility incorrect ($Name): expected=$Visible, text pixels=$ink" }
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    try {
        [void][DesktopPlusNativeSmoke]::SetCursorPos($rect.Left, ($rect.Bottom + 60))
        [DesktopPlusNativeSmoke]::Focus($list)
        Assert-Hint $true 'idle'
        foreach ($cycle in 1..3) {
            [void][DesktopPlusNativeSmoke]::SetCursorPos(($rect.Left + 20), ($rect.Top + 10))
            Assert-Hint $false "hover-$cycle"
            [void][DesktopPlusNativeSmoke]::SetCursorPos($rect.Left, ($rect.Bottom + 60))
            Assert-Hint $true "leave-$cycle"
        }
        [DesktopPlusNativeSmoke]::Focus($search)
        Assert-Hint $false 'focused'
        [void][DesktopPlusNativeSmoke]::InvalidateRect($search, [IntPtr]::Zero, $true)
        Assert-Hint $false 'focused-repaint'
        [void][DesktopPlusNativeSmoke]::SetCursorPos(($rect.Left + 20), ($rect.Top + 10))
        Assert-Hint $false 'focused-hover'
        [DesktopPlusNativeSmoke]::Focus($list)
        Assert-Hint $false 'blur-while-hovered'
        [void][DesktopPlusNativeSmoke]::SetCursorPos($rect.Left, ($rect.Bottom + 60))
        Assert-Hint $true 'blurred-leave'
        [void][DesktopPlusNativeSmoke]::SetText($search, 0xC, [IntPtr]::Zero, 'Report')
        [void][DesktopPlusNativeSmoke]::SetText($search, 0xC, [IntPtr]::Zero, '')
        Assert-Hint $true 'cleared-unfocused'
    } finally {
        [DesktopPlusNativeSmoke]::Focus($search)
        [void][DesktopPlusNativeSmoke]::SetWindowPos($Window, [IntPtr](-2), 0, 0, 0, 0, 0x13)
        [void][DesktopPlusNativeSmoke]::SetCursorPos($cursor.X, $cursor.Y)
    }
}
$process = Start-Process -FilePath $Executable -ArgumentList @("--settings", ('"' + $settingsFile + '"'), "--smoke-test", ('"' + $reportFile + '"')) -WindowStyle Hidden -PassThru
try {
    Wait-Until { ([DesktopPlusNativeSmoke]::Windows($process.Id)).ContainsKey("Smoke collapsed") } "native windows"
    $windows = [DesktopPlusNativeSmoke]::Windows($process.Id)
    [void][DesktopPlusNativeSmoke]::ShowWindow($windows["DesktopPlus"], 5)
    Wait-Until { [DesktopPlusNativeSmoke]::IsWindowVisible($windows["DesktopPlus"]) -and (Rows $windows["DesktopPlus"]) -eq 4 } "main overview"
    if ($Capture) { Screenshot $windows["DesktopPlus"] "main.png" }
    # All dashboard actions remain reachable at the minimum supported width.
    $main=$windows['DesktopPlus']
    [void][DesktopPlusNativeSmoke]::SetWindowPos($main,[IntPtr]::Zero,0,0,820,640,0x16)
    foreach ($id in @(200,201,202,203,226,227,228,229,244,245)) {
        $button=[DesktopPlusNativeSmoke]::GetDlgItem($main,$id)
        $outer=New-Object DesktopPlusNativeSmoke+Rect;$inner=New-Object DesktopPlusNativeSmoke+Rect
        [void][DesktopPlusNativeSmoke]::GetWindowRect($main,[ref]$outer)
        [void][DesktopPlusNativeSmoke]::GetWindowRect($button,[ref]$inner)
        if ($inner.Left -lt $outer.Left -or $inner.Right -gt $outer.Right -or $inner.Bottom -gt $outer.Bottom) {throw "Dashboard action $id is clipped at minimum width"}
    }
    if ($Capture) { Screenshot $main 'main-compact.png' }
    [void][DesktopPlusNativeSmoke]::SetWindowPos($main,[IntPtr]::Zero,0,0,1020,720,0x16)
    # Preferences are persistent tabs in the main workspace.
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]203,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($main,1103))} 'general tab'
    if ($Capture) { Screenshot $main 'global-general.png' }
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]229,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($main,1129))} 'appearance tab'
    $global=[DesktopPlusNativeSmoke]::GetDlgItem($main,1129)
    $globalBody=[DesktopPlusNativeSmoke]::GetDlgItem($global,801)
    [void][DesktopPlusNativeSmoke]::SetText([DesktopPlusNativeSmoke]::GetDlgItem($globalBody,1000),0xC,[IntPtr]::Zero,'invalid')
    [void][DesktopPlusNativeSmoke]::Send($global,0x111,[IntPtr]1,[IntPtr]::Zero)
    if ([DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($global,802),0xE,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -eq 0) {throw 'Appearance tab accepted an invalid color'}
    if ($Capture) { Screenshot $main 'global-validation.png' }
    [void][DesktopPlusNativeSmoke]::SetText([DesktopPlusNativeSmoke]::GetDlgItem($globalBody,1000),0xC,[IntPtr]::Zero,'#242833')
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]245,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($main,1145))} 'shortcuts tab'
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]229,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::IsWindowVisible($global)} 'return to appearance'
    if ($Capture) { Screenshot $main 'global-appearance.png' }
    [void][DesktopPlusNativeSmoke]::Send($global,0x111,[IntPtr]1,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($global,802),0xE,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -gt 0} 'saving appearance'
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]244,[IntPtr]::Zero)
    Wait-Until {[DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($main,100))} 'return to overview'
    # A short text prompt uses the same form and Cancel must not create a panel.
    [void][DesktopPlusNativeSmoke]::PostMessage($main,0x111,[IntPtr]201,[IntPtr]::Zero)
    Wait-Until {
        $candidates=[DesktopPlusNativeSmoke]::Windows($process.Id)
        $script:promptWindow=@($candidates.GetEnumerator() | Where-Object { $_.Key -ne 'DesktopPlus' -and $_.Key -notlike 'Smoke*' -and [DesktopPlusNativeSmoke]::IsWindowVisible($_.Value) -and [DesktopPlusNativeSmoke]::GetDlgItem($_.Value,801) -ne [IntPtr]::Zero } | Select-Object -First 1)
        $script:promptWindow.Count -eq 1
    } 'unified text prompt'
    $prompt=$script:promptWindow[0].Value
    if ($Capture) { Start-Sleep -Milliseconds 200; Screenshot $prompt 'new-list-dialog.png' }
    [void][DesktopPlusNativeSmoke]::Send($prompt,0x111,[IntPtr]2,[IntPtr]::Zero)
    Wait-Until {-not [DesktopPlusNativeSmoke]::IsWindow($prompt)} 'cancel new list'
    if ((Rows $main) -ne 4) {throw 'Cancelling the new-list dialog created a panel'}

    [void][DesktopPlusNativeSmoke]::ShowWindow($windows["DesktopPlus"], 6)
    $folder = $windows["Smoke folder"]
    Wait-Until { (Rows $folder) -eq 4 } "folder listing"
    Wait-Until { (Rows $windows["Smoke pinned"]) -eq 2 } "pinned items"
    Wait-Until { (Rows $windows["Smoke photos"]) -eq 1 } "photo filter"
    # Activate only the isolated fixture and exercise a real popup before checking its visible frame.
    [void][DesktopPlusNativeSmoke]::SetWindowPos($folder, [IntPtr](-1), 0, 0, 0, 0, 0x13)
    [void][DesktopPlusNativeSmoke]::SetForegroundWindow($folder)
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x86, [IntPtr]1, [IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x85, [IntPtr]1, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    Assert-DarkFrame $folder 'active-frame.png'
    $popupPoint = [IntPtr]((240 -shl 16) -bor 180)
    [void][DesktopPlusNativeSmoke]::PostMessage($folder, 0x7B, $folder, $popupPoint)
    Wait-Until { [DesktopPlusNativeSmoke]::Popup($process.Id) -ne [IntPtr]::Zero } "opening context menu"
    if ($Capture) { Screenshot ([DesktopPlusNativeSmoke]::Popup($process.Id)) "context-menu.png" }
    [void][DesktopPlusNativeSmoke]::PostMessage($folder, 0x1F, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-Until { [DesktopPlusNativeSmoke]::Popup($process.Id) -eq [IntPtr]::Zero } "closing context menu"
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x86, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    Assert-DarkFrame $folder 'context-frame.png' $false
    [void][DesktopPlusNativeSmoke]::SetWindowPos($folder, [IntPtr](-2), 0, 0, 0, 0, 0x13)
    if ([DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($windows["Smoke collapsed"], 100))) { throw "Initially collapsed panel shows its list" }
    $initialRect = New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($windows["Smoke collapsed"], [ref]$initialRect)
    if (($initialRect.Bottom - $initialRect.Top) -gt 100) { throw "Initially collapsed panel has expanded height" }
    if (($initialRect.Right - $initialRect.Left) -ne 249 -or $initialRect.Top -ne $bottomPanelTop) { throw "Saved panel width or bottom-edge position changed" }
    if ($Capture) { Screenshot $windows["Smoke collapsed"] "initial-collapsed.png" }
    $tabs = [DesktopPlusNativeSmoke]::GetDlgItem($folder, 101)
    [void][DesktopPlusNativeSmoke]::Send($tabs, 0x100, [IntPtr]0x27, [IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send($tabs, 0x101, [IntPtr]0x27, [IntPtr]::Zero)
    Wait-Until { (Rows $folder) -eq 1 } "switching to second tab"
    [void][DesktopPlusNativeSmoke]::Send($tabs, 0x100, [IntPtr]0x25, [IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send($tabs, 0x101, [IntPtr]0x25, [IntPtr]::Zero)
    Wait-Until { (Rows $folder) -eq 4 } "switching back to first tab"
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x111, [IntPtr]219, [IntPtr]::Zero)
    Wait-Until { (Height $folder) -eq 48 -and -not [DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($folder,100)) } 'settling collapse animation'
    $rect = New-Object DesktopPlusNativeSmoke+Rect
    [void][DesktopPlusNativeSmoke]::GetWindowRect($folder, [ref]$rect)
    if (($rect.Bottom - $rect.Top) -gt 100) { throw "Collapse did not reduce panel height: $($rect.Bottom - $rect.Top)" }
    foreach ($control in @(100, 102, 103)) {
        if ([DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($folder, $control))) { throw "Collapsed panel shows content control $control" }
    }
    if (-not [DesktopPlusNativeSmoke]::IsWindowVisible($tabs)) { throw "Collapsed panel lost its header tabs" }
    if ($Capture) { Screenshot $folder "collapsed.png" }
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x111, [IntPtr]219, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    if ($Capture) {
        Screenshot $folder "folder.png"
        Screenshot $windows["Smoke photos"] "photos.png"
        Screenshot $windows["Smoke pinned"] "pinned.png"
        [void][DesktopPlusNativeSmoke]::Send($windows["Smoke collapsed"], 0x111, [IntPtr]219, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        Screenshot $windows["Smoke collapsed"] "empty.png"
        [void][DesktopPlusNativeSmoke]::Send($windows["Smoke collapsed"], 0x111, [IntPtr]219, [IntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    $workingSet = $process.WorkingSet64
    $privateBytes = $process.PrivateMemorySize64
    $cpuStart = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    $idleCpu = $process.TotalProcessorTime.TotalMilliseconds - $cpuStart
    $search = [DesktopPlusNativeSmoke]::GetDlgItem($folder, 102)
    [void][DesktopPlusNativeSmoke]::Send($folder, 0x111, [IntPtr]239, [IntPtr]::Zero)
    Wait-Until { [DesktopPlusNativeSmoke]::IsWindowVisible($search) } "opening compact header search"
    Assert-SearchPlaceholder $folder
    [void][DesktopPlusNativeSmoke]::SetText($search, 0xC, [IntPtr]::Zero, "DeepSearchTarget")
    Wait-Until { (Rows $folder) -eq 1 } "recursive indexed search" 7000
    [void][DesktopPlusNativeSmoke]::SetText($search, 0xC, [IntPtr]::Zero, "")
    Wait-Until { (Rows $folder) -eq 4 } "clearing search"
    [void][DesktopPlusNativeSmoke]::Send($search, 0x100, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-Until { -not [DesktopPlusNativeSmoke]::IsWindowVisible($search) } "closing header search with Escape"
    [IO.File]::WriteAllText((Join-Path $files "WatcherAdded.txt"), "watcher test")
    Wait-Until { (Rows $folder) -eq 5 } "filesystem watcher"
    $settingsWindow=Panel-Settings $folder
    Assert-SettingsNavigation $settingsWindow
    $body=[DesktopPlusNativeSmoke]::GetDlgItem($settingsWindow,801)
    if ($Capture) { Screenshot $settingsWindow 'settings-general.png' }
    # Keyboard focus reveals controls below the fold without moving the footer.
    $lastField=[DesktopPlusNativeSmoke]::GetDlgItem($body,1006)
    [DesktopPlusNativeSmoke]::Focus($lastField)
    Wait-Until {
        $fieldRect=New-Object DesktopPlusNativeSmoke+Rect;$bodyRect=New-Object DesktopPlusNativeSmoke+Rect
        [void][DesktopPlusNativeSmoke]::GetWindowRect($lastField,[ref]$fieldRect)
        [void][DesktopPlusNativeSmoke]::GetWindowRect($body,[ref]$bodyRect)
        $fieldRect.Top -ge $bodyRect.Top -and $fieldRect.Bottom -le $bodyRect.Bottom
    } 'revealing keyboard focus below the fold'
    [DesktopPlusNativeSmoke]::Focus([DesktopPlusNativeSmoke]::GetDlgItem($body,1000))
    Wait-Until {
        $fieldRect=New-Object DesktopPlusNativeSmoke+Rect;$bodyRect=New-Object DesktopPlusNativeSmoke+Rect
        [void][DesktopPlusNativeSmoke]::GetWindowRect([DesktopPlusNativeSmoke]::GetDlgItem($body,1000),[ref]$fieldRect)
        [void][DesktopPlusNativeSmoke]::GetWindowRect($body,[ref]$bodyRect)
        $fieldRect.Top -ge $bodyRect.Top
    } 'revealing first field again'

    $hidden=[DesktopPlusNativeSmoke]::GetDlgItem($body,1005)
    if ($Capture) {
        [void][DesktopPlusNativeSmoke]::Send($hidden,0x200,[IntPtr]::Zero,[IntPtr]0x00140020)
        Screenshot $settingsWindow 'settings-hover.png'
        [void][DesktopPlusNativeSmoke]::Send($hidden,0x2A3,[IntPtr]::Zero,[IntPtr]::Zero)
    }
    [void][DesktopPlusNativeSmoke]::Send($hidden,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    if ([DesktopPlusNativeSmoke]::Send($hidden,0xF0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -ne 0) { throw 'Styled checkbox did not toggle' }
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]2,[IntPtr]::Zero)
    Wait-Until { -not [DesktopPlusNativeSmoke]::IsWindow($settingsWindow) } 'cancel settings'
    $settingsWindow=Panel-Settings $folder
    $body=[DesktopPlusNativeSmoke]::GetDlgItem($settingsWindow,801)
    if ([DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1005),0xF0,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -ne 1) { throw 'Cancel changed the hidden-files setting' }
    [void][DesktopPlusNativeSmoke]::SetText([DesktopPlusNativeSmoke]::GetDlgItem($body,1000),0xC,[IntPtr]::Zero,'')
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]1,[IntPtr]::Zero)
    if (-not [DesktopPlusNativeSmoke]::IsWindowVisible($settingsWindow)) { throw 'Invalid panel name was accepted' }
    [void][DesktopPlusNativeSmoke]::SetText([DesktopPlusNativeSmoke]::GetDlgItem($body,1000),0xC,[IntPtr]::Zero,'Smoke folder')
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]701,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1008),0x14E,[IntPtr]1,[IntPtr]::Zero)
    if ($Capture) { Screenshot $settingsWindow 'settings-view.png' }
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]702,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1017),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    if ($Capture) { Screenshot $settingsWindow 'settings-header.png' }
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]703,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1019),0x14E,[IntPtr]2,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1020),0x14E,[IntPtr]1,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1021),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1022),0x14E,[IntPtr]1,[IntPtr]::Zero)
    if ($Capture) { Screenshot $settingsWindow 'settings-behavior.png' }
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]704,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1024),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    if ($Capture) { Screenshot $settingsWindow 'settings-columns.png' }
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]1,[IntPtr]::Zero)
    Wait-Until { -not [DesktopPlusNativeSmoke]::IsWindow($settingsWindow) } 'saving panel settings'
    Wait-Until { (Rows $folder) -eq 6 } 'parent navigation item'
    if ([DesktopPlusNativeSmoke]::IsWindowVisible([DesktopPlusNativeSmoke]::GetDlgItem($folder,231))) { throw 'Close-button preference did not apply' }
    $list=[DesktopPlusNativeSmoke]::GetDlgItem($folder,100)
    if ([DesktopPlusNativeSmoke]::Send($list,0x101D,[IntPtr]2,[IntPtr]::Zero).ToInt32() -ne 0) { throw 'Size column remains visible' }
    $lockedPoint=[IntPtr]((100 -shl 16) -bor 42)
    if ([DesktopPlusNativeSmoke]::Send($folder,0x84,[IntPtr]::Zero,$lockedPoint).ToInt32() -ne 1) { throw 'Locked panel still exposes resize edges' }
    [void][DesktopPlusNativeSmoke]::Send($folder,0xA3,[IntPtr]2,[IntPtr]::Zero)
    if ((Height $folder) -lt 100) { throw 'Button-only collapse responds to a double-click' }
    if ($Capture) { Screenshot $folder 'settings-applied.png' }
    $settingsWindow=Panel-Settings $folder
    $body=[DesktopPlusNativeSmoke]::GetDlgItem($settingsWindow,801)
    [void][DesktopPlusNativeSmoke]::Send($settingsWindow,0x111,[IntPtr]701,[IntPtr]::Zero)
    [void][DesktopPlusNativeSmoke]::Send([DesktopPlusNativeSmoke]::GetDlgItem($body,1010),0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-Until { -not [DesktopPlusNativeSmoke]::IsWindow($settingsWindow) -and (Height $folder) -ge 220 -and (Height $folder) -lt 500 } 'fitting panel height to loaded rows'
    $cursor=New-Object DesktopPlusNativeSmoke+Point
    [void][DesktopPlusNativeSmoke]::GetCursorPos([ref]$cursor)
    try {
        [void][DesktopPlusNativeSmoke]::SetCursorPos(670,60)
        [void][DesktopPlusNativeSmoke]::Send($folder,0x111,[IntPtr]219,[IntPtr]::Zero)
        Wait-Until { (Height $folder) -eq 48 } 'collapse before hover'
        Wait-Until {
            $saved=Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            $saved.Windows[0].IsCollapsed -and $saved.Windows[0].Height -eq 48
        } 'saving the collapsed geometry before hover'
        [void][DesktopPlusNativeSmoke]::SetWindowPos($folder,[IntPtr](-1),0,0,0,0,0x43)
        [void][DesktopPlusNativeSmoke]::SetForegroundWindow($folder)
        [DesktopPlusNativeSmoke]::Expose($folder)
        [void][DesktopPlusNativeSmoke]::DwmFlush()
        Wait-Until {
            [void][DesktopPlusNativeSmoke]::SetCursorPos(120,104)
            [void][DesktopPlusNativeSmoke]::Send($folder,0x20,$folder,[IntPtr]0x2000001)
            (Height $folder) -gt 100
        } 'expand on mouse hover'
        if ($Capture) { Screenshot $folder 'hover-expanded.png' }
        Start-Sleep -Milliseconds 210
        $expandedHeight=Height $folder
        $expandedBounds=New-Object DesktopPlusNativeSmoke+Rect
        [void][DesktopPlusNativeSmoke]::GetWindowRect($folder,[ref]$expandedBounds)
        # Switching tabs must preserve hover expansion, including its saved return bounds.
        foreach ($switch in @(@{ Key=0x27; Index=1; Rows=2 }, @{ Key=0x25; Index=0; Rows=6 })) {
            [void][DesktopPlusNativeSmoke]::Send($tabs,0x100,[IntPtr]$switch.Key,[IntPtr]::Zero)
            [void][DesktopPlusNativeSmoke]::Send($tabs,0x101,[IntPtr]$switch.Key,[IntPtr]::Zero)
            $tabWatch=[Diagnostics.Stopwatch]::StartNew()
            do {
                $tabBounds=New-Object DesktopPlusNativeSmoke+Rect
                [void][DesktopPlusNativeSmoke]::GetWindowRect($folder,[ref]$tabBounds)
                if ($tabBounds.Left -ne $expandedBounds.Left -or $tabBounds.Top -ne $expandedBounds.Top -or
                    $tabBounds.Right -ne $expandedBounds.Right -or $tabBounds.Bottom -ne $expandedBounds.Bottom -or
                    -not [DesktopPlusNativeSmoke]::IsWindowVisible($list)) {
                    throw 'Switching tabs interrupted hover expansion'
                }
                Start-Sleep -Milliseconds 10
            } while ($tabWatch.ElapsedMilliseconds -lt 650)
            Wait-Until { (Rows $folder) -eq $switch.Rows } 'loading the tab while hover-expanded'
            $saved=Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
            if ($saved.Windows[0].ActiveTabIndex -ne $switch.Index -or -not $saved.Windows[0].IsCollapsed -or
                $saved.Windows[0].Height -ne 48) { throw 'Tab switching persisted temporary hover geometry' }
        }
        if ($Capture) { Screenshot $folder 'hover-tab-switch.png' }
        [void][DesktopPlusNativeSmoke]::SetCursorPos(670,60)
        Start-Sleep -Milliseconds 55
        [void][DesktopPlusNativeSmoke]::SetCursorPos(120,104)
        [void][DesktopPlusNativeSmoke]::Send($folder,0x20,$folder,[IntPtr]0x2000001)
        Wait-Until { (Height $folder) -eq $expandedHeight } 'reversing hover collapse on re-entry'
        $leave=[Diagnostics.Stopwatch]::StartNew()
        [void][DesktopPlusNativeSmoke]::SetCursorPos(670,60)
        Wait-Until { (Height $folder) -eq 48 } 'collapse after pointer leaves'
        $hoverCloseMs=$leave.ElapsedMilliseconds
        if ($hoverCloseMs -gt 350) {throw "Hover collapse is delayed: $hoverCloseMs ms"}
    } finally { [void][DesktopPlusNativeSmoke]::SetCursorPos($cursor.X,$cursor.Y) }
    # Rounded geometry can reach its endpoint just before the final timer tick.
    # Also allow any hover caused by restoring the real cursor to finish.
    Start-Sleep -Milliseconds 250
    # Exercise the normal debounced save before the smoke-only exit, which
    # deliberately skips the application's normal shutdown write.
    Wait-Until {
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $saved.Windows[0].IsCollapsed -and $saved.Windows[0].Tabs[0].ExpandOnHover
    } 'persisting panel settings'
    [void][DesktopPlusNativeSmoke]::PostMessage($windows['DesktopPlus'],0x113,[IntPtr]3,[IntPtr]::Zero)
    if (-not $process.WaitForExit(15000)) { throw "Native app did not finish smoke test" }
    if ($process.ExitCode -ne 0) { throw "Native process exited with $($process.ExitCode)" }
    $report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
    if ($report.ui -ne "Win32" -or $report.runtime -ne "Rust" -or $report.windows -ne 5) { throw "Unexpected native report" }
    if ($report.errors.Count -ne 0) { throw "Native errors: $($report.errors -join ', ')" }
    if ($report.active_animations -ne 0) {throw 'Animation timers did not settle'}
    if ($report.thumbnails -lt 1) { throw "No photo thumbnail was rendered" }
    if ($report.metadata -lt 1) { throw "Metadata properties were not loaded" }
    if ($report.icons -lt 1) { throw "Application shortcut icon was not loaded" }
    $persisted = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    if ($persisted.Windows.Count -ne 4 -or -not $persisted.Windows[0].IsCollapsed) { throw "Panel settings were not saved" }
    if (-not $persisted.Windows[0].Tabs[0].ExpandOnHover -or $persisted.Windows[0].Tabs[0].MovementMode -ne 'locked' -or $persisted.Windows[0].PresetName -ne 'Smoke forest') { throw 'Panel behavior or preset was not persisted' }
    $report | Add-Member -NotePropertyName working_set_bytes -NotePropertyValue $workingSet
    $report | Add-Member -NotePropertyName private_bytes -NotePropertyValue $privateBytes
    $report | Add-Member -NotePropertyName idle_cpu_ms_per_second -NotePropertyValue $idleCpu
    $report | Add-Member -NotePropertyName hover_close_ms -NotePropertyValue $hoverCloseMs
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportFile -Encoding UTF8
    Write-Host "Native UI smoke passed: $reportFile"
    Write-Host "Working set: $([Math]::Round($workingSet / 1MB, 1)) MiB; private: $([Math]::Round($privateBytes / 1MB, 1)) MiB; idle CPU: $idleCpu ms/s"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
