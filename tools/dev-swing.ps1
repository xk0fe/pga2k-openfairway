<# Developer helper: a mouse swing for the game (pull the mouse back, then push it forward), sent as real
   relative mouse movement. Refuses unless the game window is in the foreground. #>
param([int]$Back = 400, [int]$Forward = 700, [int]$BackMs = 500, [int]$ForwardMs = 220, [switch]$Hold)
$NoGameFolder = $true
. (Join-Path $PSScriptRoot 'game-path.ps1')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DevSwing {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
$p = Get-OpenFairwayGameProcess; $h = $p.MainWindowHandle
for ($try = 0; $try -lt 3 -and [DevSwing]::GetForegroundWindow() -ne $h; $try++) {
    [DevSwing]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [DevSwing]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][DevSwing]::ShowWindow($h, 9); [void][DevSwing]::SetForegroundWindow($h); Start-Sleep -Milliseconds 400
}
if ([DevSwing]::GetForegroundWindow() -ne $h) { "refused: the game window is not in front"; exit 1 }
if ($Hold) { [DevSwing]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero) }
$steps = 25
for ($i = 0; $i -lt $steps; $i++) { [DevSwing]::mouse_event(1, 0, [int]($Back / $steps), 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds ([int]($BackMs / $steps)) }
Start-Sleep -Milliseconds 80
for ($i = 0; $i -lt $steps; $i++) { [DevSwing]::mouse_event(1, 0, -[int]($Forward / $steps), 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds ([int]($ForwardMs / $steps)) }
if ($Hold) { [DevSwing]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
"swung"
