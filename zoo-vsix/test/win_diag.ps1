# 诊断:spawned devenv 的窗口到底在哪、句柄是否有效
$ErrorActionPreference = "Continue"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$vs = Start-Process "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe" -ArgumentList "`"$($projDir)\DebugTarget.csproj`"" -PassThru
Write-Host ("spawned PID=" + $vs.Id)
Start-Sleep -Seconds 75
$proc = Get-Process -Id $vs.Id -ErrorAction SilentlyContinue
if (-not $proc) { Write-Host "process EXITED"; exit }
Write-Host ("HasExited=" + $proc.HasExited + " MainWindowHandle=" + $proc.MainWindowHandle + " Title=[" + $proc.MainWindowTitle + "]")

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinDiag {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<string> WindowsOfPid(uint target) {
        var result = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                RECT r; GetWindowRect(h, out r);
                result.Add(h.ToInt64() + " [" + sb + "] " + r.Left + "," + r.Top + " - " + r.Right + "," + r.Bottom);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@
Write-Host "--- visible windows of PID $($vs.Id) ---"
[WinDiag]::WindowsOfPid([uint32]$vs.Id) | ForEach-Object { Write-Host $_ }
$fg = [WinDiag]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[WinDiag]::GetWindowText($fg, $sb, 256) | Out-Null
Write-Host ("foreground hwnd=" + $fg + " [" + $sb.ToString() + "]")

try { $vs.Kill() } catch {}
