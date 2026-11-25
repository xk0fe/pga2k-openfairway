<# Developer helper: a real left click at a point inside the game window (client coordinates).
   Refuses to click unless the game window is in the foreground and is the window under that point,
   so a click can never land in another application. #>
param([int]$X, [int]$Y)
$NoGameFolder = $true
. (Join-Path $PSScriptRoot 'game-path.ps1')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DevMouse {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
$p = Get-OpenFairwayGameProcess
$h = $p.MainWindowHandle
$pt = New-Object DevMouse+POINT; $pt.X = $X; $pt.Y = $Y
[void][DevMouse]::ClientToScreen($h, [ref]$pt)

for ($try = 0; $try -lt 3 -and [DevMouse]::GetForegroundWindow() -ne $h; $try++) {
    # Windows only lets a process take the foreground after recent input; a tap of Alt satisfies that.
    [DevMouse]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [DevMouse]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][DevMouse]::ShowWindow($h, 9)
    [void][DevMouse]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
}
$under = [DevMouse]::GetAncestor([DevMouse]::WindowFromPoint($pt), 2)
if ([DevMouse]::GetForegroundWindow() -ne $h -or $under -ne $h) {
    "NOT CLICKED: the game window is not in front at that point"
    exit 1
}
[void][DevMouse]::SetCursorPos($pt.X, $pt.Y); Start-Sleep -Milliseconds 250
[void][DevMouse]::SetCursorPos($pt.X + 2, $pt.Y + 1); Start-Sleep -Milliseconds 250
if ([DevMouse]::GetForegroundWindow() -ne $h) { "NOT CLICKED: focus changed"; exit 1 }
[DevMouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
[DevMouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
"clicked $($pt.X),$($pt.Y)"
