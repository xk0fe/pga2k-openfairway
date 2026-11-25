<# Developer helper: presses and releases one key (virtual-key code) for the game.
   Refuses unless the game window is in the foreground, so a key can never land in another application. #>
param([int]$Key, [int]$HoldMs = 60)
$NoGameFolder = $true
. (Join-Path $PSScriptRoot 'game-path.ps1')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class DevKey {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@
$p = Get-OpenFairwayGameProcess
$h = $p.MainWindowHandle
for ($try = 0; $try -lt 3 -and [DevKey]::GetForegroundWindow() -ne $h; $try++) {
    [DevKey]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [DevKey]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][DevKey]::ShowWindow($h, 9); [void][DevKey]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
}
if ([DevKey]::GetForegroundWindow() -ne $h) { "refused: the game window is not in front"; exit 1 }
[DevKey]::keybd_event([byte]$Key, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds $HoldMs
[DevKey]::keybd_event([byte]$Key, 0, 2, [UIntPtr]::Zero)
"pressed $Key"
