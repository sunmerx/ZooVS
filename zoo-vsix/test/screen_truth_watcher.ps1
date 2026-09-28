# 屏幕真相捕获器:监测 inline-completion.log 出现"渲染建议"的瞬间,立即截取用户 VS 实际画面
# 目的:判定"日志渲染了 vs 用户屏幕看不见"的分歧(装饰层在视觉树里 vs 像素在屏幕上)
$ErrorActionPreference = "Continue"
$logFile = "$env:LOCALAPPDATA\ZooVS\log\inline-completion.log"
$outDir = "E:\code\cline-vsex\zoo-vsix\test"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$lastCount = 0
if (Test-Path $logFile) { $lastCount = (Get-Content $logFile -ErrorAction SilentlyContinue | Measure-Object -Line).Lines }
Write-Host ("[watch] start, baseline lines=" + $lastCount)
$deadline = (Get-Date).AddMinutes(90)
$shots = 0
while ((Get-Date) -lt $deadline -and $shots -lt 12) {
    Start-Sleep -Milliseconds 400
    $lines = @(Get-Content $logFile -ErrorAction SilentlyContinue)
    if ($lines.Count -le $lastCount) { continue }
    $new = $lines[$lastCount..($lines.Count - 1)]
    $lastCount = $lines.Count
    $hit = $new | Where-Object { $_ -match "渲染建议" } | Select-Object -First 1
    if (-not $hit) { continue }
    Start-Sleep -Milliseconds 400   # 等 WPF 合成一帧
    $procs = Get-Process devenv -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    foreach ($p in $procs) {
        try {
            $r = New-Object WinCap+RECT
            [WinCap]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
            $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
            if ($w -le 0 -or $ht -le 0) { continue }
            $bmp = New-Object System.Drawing.Bitmap($w, $ht)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $hdc = $g.GetHdc()
            $ok = [WinCap]::PrintWindow($p.MainWindowHandle, $hdc, 2)
            $g.ReleaseHdc($hdc)
            $ts = Get-Date -Format "HHmmss_fff"
            $out = Join-Path $outDir ("userscreen_" + $ts + "_pid" + $p.Id + ".png")
            $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            $shots++
            Write-Host ("[shot] " + $out + " PrintWindow=" + $ok + " trigger=" + $hit)
        } catch { Write-Host ("[warn] " + $_.Exception.Message) }
    }
}
Write-Host ("[watch] end, shots=" + $shots)
