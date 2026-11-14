param([string]$Executable='target/debug/DesktopPlus.exe',[switch]$HoverOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$Executable=(Resolve-Path -LiteralPath $Executable).Path
$run=Join-Path $repo ('artifacts/display-tests/'+[guid]::NewGuid().ToString('N'))
$icons=Join-Path $run 'shortcuts';$photos=Join-Path $run 'photos';$details=Join-Path $run 'details'
New-Item -ItemType Directory -Force -Path $icons,$photos,$details | Out-Null
Add-Type -AssemblyName System.Drawing
# Two icon resolutions with different colors verify that the shell loads the
# real high-resolution resource, rather than a generic or enlarged 16px icon.
$pngs=@()
foreach ($size in @(16,256)) {
    $bitmap=New-Object Drawing.Bitmap $size,$size
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear($(if ($size -eq 256) {[Drawing.Color]::FromArgb(40,180,90)} else {[Drawing.Color]::Red}))
    $stream=New-Object IO.MemoryStream
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $pngs+=,@{Size=$size;Bytes=$stream.ToArray()}
    $graphics.Dispose();$bitmap.Dispose();$stream.Dispose()
}
$ico=Join-Path $run 'original.ico';$stream=[IO.File]::Create($ico);$writer=New-Object IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]2);$offset=38
    foreach ($png in $pngs) {
        $writer.Write([byte]($png.Size % 256));$writer.Write([byte]($png.Size % 256));$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$png.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$png.Bytes.Length
    }
    foreach ($png in $pngs) {$writer.Write([byte[]]$png.Bytes)}
} finally {$writer.Dispose();$stream.Dispose()}
$shell=New-Object -ComObject WScript.Shell
$link=$shell.CreateShortcut((Join-Path $icons '00 Application.lnk'));$link.TargetPath=Join-Path $env:WINDIR 'System32/notepad.exe';$link.IconLocation="$ico,0";$link.Save()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
foreach ($i in 1..29) {[IO.File]::WriteAllText((Join-Path $icons ('{0:D2} Game.url' -f $i)),"[InternetShortcut]`r`nURL=https://example.invalid/$i`r`nIconFile=$ico`r`nIconIndex=0`r`n")}
foreach ($i in 1..80) {[IO.File]::WriteAllText((Join-Path $details ('Document {0:D2}.txt' -f $i)),'Display fixture')}
$bitmap=New-Object Drawing.Bitmap 2048,2048;$graphics=[Drawing.Graphics]::FromImage($bitmap);$graphics.Clear([Drawing.Color]::SteelBlue)
$graphics.DrawEllipse([Drawing.Pens]::White,200,200,1600,1600);$bitmap.Save((Join-Path $photos 'Photo.png'),[Drawing.Imaging.ImageFormat]::Png);$graphics.Dispose();$bitmap.Dispose()
$settings=Join-Path $run 'settings.json';$report=Join-Path $run 'report.json'
@{ Language='de';CloseBehavior='Exit';Windows=@(
    @{PanelTitle='Display icons';FolderPath=$icons;DefaultFolderPath=$icons;ViewMode='icons';SearchVisibilityMode='button';Left=40;Top=80;Width=420;Height=480},
    @{PanelTitle='Display details';FolderPath=$details;DefaultFolderPath=$details;ViewMode='details';Left=600;Top=80;Width=620;Height=480},
    @{PanelTitle='Display photos';FolderPath=$photos;DefaultFolderPath=$photos;ViewMode='photos';Left=1250;Top=80;Width=500;Height=500}
)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settings -Encoding UTF8
Add-Type -ReferencedAssemblies System.Drawing @'
using System;using System.Text;using System.Runtime.InteropServices;using System.Collections.Generic;
public static class DPDisplayTest {
 public delegate bool CB(IntPtr h,IntPtr l);
 [DllImport("user32.dll")]static extern bool EnumWindows(CB cb,IntPtr l);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder t,int n);
 [DllImport("user32.dll")]public static extern IntPtr GetDlgItem(IntPtr h,int id);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]public static extern IntPtr Send(IntPtr h,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int cmd);
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")]public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr h,IntPtr z,int x,int y,int cx,int cy,uint flags);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern bool GetScrollInfo(IntPtr h,int bar,ref Scroll info);
 [DllImport("user32.dll")]public static extern bool GetScrollBarInfo(IntPtr h,int bar,ref Bar info);
 [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point p);
 [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr h,uint flags);
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)]public struct Scroll{public uint Size,Mask;public int Min,Max;public uint Page;public int Pos,Track;}
 [StructLayout(LayoutKind.Sequential)]public struct Bar{public uint Size;public Rect Area;public int Button,ThumbTop,ThumbBottom,Reserved;[MarshalAs(UnmanagedType.ByValArray,SizeConst=6)]public uint[] State;}
 public static Dictionary<string,IntPtr> Windows(int pid){var a=new Dictionary<string,IntPtr>();EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid){var t=new StringBuilder(512);GetWindowText(h,t,512);if(t.Length>0)a[t.ToString()]=h;}return true;},IntPtr.Zero);return a;}
 public static int[] HoverFrames(IntPtr control,Rect area,bool green,string failureImage) {
  Rect window;GetWindowRect(control,out window);
  IntPtr root=GetAncestor(control,2);
  SetWindowPos(root,new IntPtr(-1),0,0,0,0,0x43);
  SetCursorPos(window.Right+8,window.Top+8);
  System.Threading.Thread.Sleep(220);
  using(var bitmap=new System.Drawing.Bitmap(area.Right-area.Left,area.Bottom-area.Top))
  using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) {
   int baseline=0,minimum=int.MaxValue;
   for(int frame=0;frame<97;frame++) {
    if(frame>0 && (frame-1)%12==0) {
     bool inside=((frame-1)/12)%2==0;
     SetCursorPos(inside?(area.Left+area.Right)/2:window.Right+8,inside?(area.Top+area.Bottom)/2:window.Top+8);
    }
    // Sample existing screen pixels during interrupted transitions. PrintWindow
    // or a forced repaint would hide the incomplete frames being tested.
    var center=new Point{X=(area.Left+area.Right)/2,Y=(area.Top+area.Bottom)/2};
    if(GetAncestor(WindowFromPoint(center),2)!=root)
     throw new Exception("Hover test window is covered by another window; rerun without overlapping UI tests.");
    graphics.CopyFromScreen(area.Left,area.Top,0,0,bitmap.Size);
    int ink=0;
    for(int x=0;x<bitmap.Width;x++)for(int y=0;y<bitmap.Height;y++) {
     var p=bitmap.GetPixel(x,y);
     if(green?p.G>150&&p.R<70&&p.B<120:p.R>110&&p.G>110&&p.B>110)ink++;
    }
    if(frame==0)baseline=ink;
    minimum=Math.Min(minimum,ink);
    if(baseline<12 || ink<baseline*0.8) {
     bitmap.Save(failureImage,System.Drawing.Imaging.ImageFormat.Png);
     throw new Exception("Hover lost icon pixels at frame "+frame+": "+ink+" / "+baseline);
    }
    System.Threading.Thread.Sleep(4);
   }
   return new[]{baseline,minimum,96};
  }
 }
}
'@
function Wait-For([scriptblock]$Test,[string]$Description) {$timer=[Diagnostics.Stopwatch]::StartNew();while (-not (& $Test)) {if ($timer.ElapsedMilliseconds -gt 6000) {throw "Timed out: $Description"};Start-Sleep -Milliseconds 50}}
function Bounds([IntPtr]$h) {$r=New-Object DPDisplayTest+Rect;[void][DPDisplayTest]::GetWindowRect($h,[ref]$r);return $r}
function Width([IntPtr]$h) {$r=Bounds $h;return $r.Right-$r.Left}
function Height([IntPtr]$h) {$r=Bounds $h;return $r.Bottom-$r.Top}
function Scroll([IntPtr]$h,[int]$axis=1) {$s=New-Object DPDisplayTest+Scroll;$s.Size=28;$s.Mask=0x17;[void][DPDisplayTest]::GetScrollInfo($h,$axis,[ref]$s);return $s}
function Zoom([IntPtr]$h,[int]$steps) {$delta=($steps*120)-band 0xFFFF;[void][DPDisplayTest]::Send($h,0x20A,[IntPtr](($delta -shl 16)-bor 8),[IntPtr]::Zero)}
function Capture([IntPtr]$h,[string]$name) {$r=Bounds $h;$b=New-Object Drawing.Bitmap ($r.Right-$r.Left),($r.Bottom-$r.Top);$g=[Drawing.Graphics]::FromImage($b);$dc=$g.GetHdc();try{[void][DPDisplayTest]::PrintWindow($h,$dc,2)}finally{$g.ReleaseHdc($dc)};$b.Save((Join-Path $run $name),[Drawing.Imaging.ImageFormat]::Png);$g.Dispose();$b.Dispose()}
$process=Start-Process -FilePath $Executable -ArgumentList @('--settings',('"'+$settings+'"'),'--smoke-test',('"'+$report+'"')) -WindowStyle Hidden -PassThru
$cursor=New-Object DPDisplayTest+Point;[void][DPDisplayTest]::GetCursorPos([ref]$cursor)
try {
    Wait-For {([DPDisplayTest]::Windows($process.Id)).ContainsKey('Display photos')} 'windows'
    $windows=[DPDisplayTest]::Windows($process.Id);$panel=$windows['Display icons'];$list=[DPDisplayTest]::GetDlgItem($panel,100)
    [void][DPDisplayTest]::ShowWindow($windows['DesktopPlus'],0)
    foreach ($name in @('Display icons','Display details','Display photos')) {[void][DPDisplayTest]::ShowWindow($windows[$name],4)}
    Wait-For {[DPDisplayTest]::Send($list,0x1004,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -eq 30} 'shortcut listing'
    Wait-For {
        Capture $panel 'icons-100.png'
        $bitmap=[Drawing.Bitmap]::FromFile((Join-Path $run 'icons-100.png'));$green=@(0,0)
        try {foreach ($x in 20..195) {foreach ($y in 60..120) {$p=$bitmap.GetPixel($x,$y);if ($p.G -gt 150 -and $p.R -lt 70 -and $p.B -lt 120){$green[[int]($x -ge 110)]++}}}}finally{$bitmap.Dispose()}
        $green[0] -gt 200 -and $green[1] -gt 200
    } 'high resolution application and URL shortcut icons'
    [void][DPDisplayTest]::SetWindowPos($panel,[IntPtr](-1),0,0,0,0,0x43)
    [void][DPDisplayTest]::SetForegroundWindow($panel)
    $hoverResults=@()
    foreach ($id in @(214,239,219,231)) {
        $button=[DPDisplayTest]::GetDlgItem($panel,$id)
        $samples=[DPDisplayTest]::HoverFrames($button,(Bounds $button),$false,(Join-Path $run "hover-button-$id-failure.png"))
        $hoverResults+=@{control=$id;baseline=$samples[0];minimum=$samples[1];frames=$samples[2]}
    }
    $area=Bounds $list;$area.Left+=20;$area.Top+=8;$area.Right=$area.Left+60;$area.Bottom=$area.Top+52
    $samples=[DPDisplayTest]::HoverFrames($list,$area,$true,(Join-Path $run 'hover-file-icon-failure.png'))
    $hoverResults+=@{control='file-icon';baseline=$samples[0];minimum=$samples[1];frames=$samples[2]}
    $hoverResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'hover-frames.json') -Encoding UTF8
    [void][DPDisplayTest]::SetWindowPos($panel,[IntPtr](-2),0,0,0,0,0x13)
    if ($HoverOnly) {
        Write-Host "Native hover passed: $(Join-Path $run 'hover-frames.json')"
        return
    }
    $grip=[DPDisplayTest]::GetDlgItem($panel,242)
    if ($grip -eq [IntPtr]::Zero) {throw 'Resize grip is missing'}
    $handle=Bounds $grip;$dragX=$handle.Left+10;$dragY=$handle.Top+10
    [void][DPDisplayTest]::SetCursorPos($dragX,$dragY)
    [void][DPDisplayTest]::Send($grip,0x201,[IntPtr]1,[IntPtr]::Zero)
    [void][DPDisplayTest]::SetCursorPos(($dragX+30),($dragY+20))
    [void][DPDisplayTest]::Send($grip,0x200,[IntPtr]1,[IntPtr]::Zero)
    [void][DPDisplayTest]::Send($grip,0x202,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-For {(Width $panel) -eq 450 -and (Height $panel) -eq 500} 'manual resize grip drag'
    [void][DPDisplayTest]::Send($grip,0x203,[IntPtr]1,[IntPtr]::Zero)
    Wait-For {(Width $panel) -lt 450} 'first double-click fits width'
    $fittedWidth=Width $panel
    [void][DPDisplayTest]::Send($grip,0x203,[IntPtr]1,[IntPtr]::Zero)
    Wait-For {(Height $panel) -gt 500} 'second double-click fits height'
    if ((Width $panel) -ne $fittedWidth) {throw 'Repeated fit changed the already fitted width'}
    Capture $panel 'fit-content.png'
    Zoom $list 100
    Wait-For {([DPDisplayTest]::Send($list,0x1033,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()-band 0xFFFF) -eq 200} '200 percent icon zoom'
    Start-Sleep -Milliseconds 300
    Capture $panel 'icons-200.png'
    [void][DPDisplayTest]::PostMessage($list,0x201,[IntPtr]1,[IntPtr]((174 -shl 16)-bor 100))
    [void][DPDisplayTest]::PostMessage($list,0x202,[IntPtr]::Zero,[IntPtr]((174 -shl 16)-bor 100))
    Wait-For {[DPDisplayTest]::Send($list,0x100C,[IntPtr](-1),[IntPtr]2).ToInt32() -eq 0} 'second text line selects its item'
    $before=(Scroll $list).Pos
    [void][DPDisplayTest]::Send($list,0x20A,[IntPtr](0xFF880000L),[IntPtr]::Zero)
    Wait-For {(Scroll $list).Pos -gt $before} 'ordinary mouse wheel scrolls'
    [void][DPDisplayTest]::SetWindowPos($panel,[IntPtr](-1),0,0,0,0,0x43);[void][DPDisplayTest]::SetForegroundWindow($panel)
    $bar=New-Object DPDisplayTest+Bar;$bar.Size=[Runtime.InteropServices.Marshal]::SizeOf($bar)
    [void][DPDisplayTest]::GetScrollBarInfo($list,-5,[ref]$bar)
    $x=[int](($bar.Area.Left+$bar.Area.Right)/2);$y=$bar.Area.Top+[int](($bar.ThumbTop+$bar.ThumbBottom)/2)
    [void][DPDisplayTest]::SetCursorPos($x,$y);[void][DPDisplayTest]::Send($list,0xA1,[IntPtr]7,[IntPtr](($y -shl 16)-bor $x))
    $before=(Scroll $list).Pos
    [void][DPDisplayTest]::SetCursorPos($x,$bar.Area.Bottom-20);[void][DPDisplayTest]::Send($list,0x200,[IntPtr]1,[IntPtr]::Zero)
    [void][DPDisplayTest]::Send($list,0x202,[IntPtr]::Zero,[IntPtr]::Zero)
    Wait-For {(Scroll $list).Pos -gt $before} 'scrollbar thumb drag'
    Capture $panel 'scrollbar.png'
    Zoom $list -100
    Wait-For {([DPDisplayTest]::Send($list,0x1033,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()-band 0xFFFF) -eq 70} '70 percent zoom lower limit'
    Zoom $list 100
    $detailsList=[DPDisplayTest]::GetDlgItem($windows['Display details'],100)
    $baseWidth=[DPDisplayTest]::Send($detailsList,0x101D,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32()
    Zoom $detailsList 20
    Wait-For {[DPDisplayTest]::Send($detailsList,0x101D,[IntPtr]::Zero,[IntPtr]::Zero).ToInt32() -eq 2*$baseWidth} 'details zoom scales column layout'
    $photosList=[DPDisplayTest]::GetDlgItem($windows['Display photos'],100);Zoom $photosList 20
    Wait-For {([DPDisplayTest]::Send($photosList,0x1033,[IntPtr]::Zero,[IntPtr]::Zero).ToInt64()-band 0xFFFF) -eq 432} 'photo zoom'
    Start-Sleep -Milliseconds 400
    Capture $windows['Display details'] 'details-200.png';Capture $windows['Display photos'] 'photos-200.png'
    [void][DPDisplayTest]::Send($windows['DesktopPlus'],0x113,[IntPtr]1,[IntPtr]::Zero)
    Wait-For {(Get-Content $settings -Raw | ConvertFrom-Json).Windows[0].Zoom -eq 2.0} 'persisted panel zoom'
    [void][DPDisplayTest]::PostMessage($windows['DesktopPlus'],0x113,[IntPtr]3,[IntPtr]::Zero)
    if (-not $process.WaitForExit(10000)) {throw 'Smoke shutdown timed out'}
    $result=Get-Content $report -Raw | ConvertFrom-Json
    if ($result.errors.Count -or $result.text_renderer -ne 'DirectWrite') {throw 'Native rendering error'}
    if ($result.active_animations -ne 0) {throw 'Visible rows and scrollbar animations did not settle'}
    if (($result.panels | Where-Object title -eq 'Display icons').image_pixels -ne 96 -or ($result.panels | Where-Object title -eq 'Display photos').image_pixels -ne 384) {throw 'Images did not request the physical zoom resolution'}
    Write-Host "Native display passed: $report"
} finally {
    [void][DPDisplayTest]::SetCursorPos($cursor.X,$cursor.Y)
    if (-not $process.HasExited) {Stop-Process -Id $process.Id -Force}
    $process.Dispose()
}
