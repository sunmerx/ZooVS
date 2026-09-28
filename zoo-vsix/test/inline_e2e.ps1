# 行内补全端到端实测:开 VS -> 打开 Program.cs -> 模拟输入 -> 截图(退出前) -> 读日志
# 关键约束:多 devenv 实例并存时,一切操作(前台/截图/DTE/退出)只针对本脚本启动的 PID,
# 不触碰用户自己打开的 VS。
$ErrorActionPreference = "Continue"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$program = Join-Path $projDir "Program.cs"
$logFile = "$env:LOCALAPPDATA\ZooVS\log\inline-completion.log"
$shotPath = "E:\code\cline-vsex\zoo-vsix\test\inline_shot.png"
Remove-Item $logFile -ErrorAction SilentlyContinue

$vs = Start-Process "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe" -ArgumentList "`"$($projDir)\DebugTarget.csproj`"" -PassThru
Write-Host ("[info] spawned devenv PID=" + $vs.Id)

# DTE 按 ROT 显示名精确连接到本实例(枚举 ROT,匹配 !VisualStudio.DTE.17.0:<pid>)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public class RotHelper {
    [DllImport("ole32.dll")] public static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] public static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    public static object GetDteByPid(int pid) {
        IRunningObjectTable rot; IEnumMoniker enumMon;
        if (GetRunningObjectTable(0, out rot) != 0) return null;
        rot.EnumRunning(out enumMon);
        var monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;
        IBindCtx ctx;
        if (CreateBindCtx(0, out ctx) != 0) return null;
        while (enumMon.Next(1, monikers, fetched) == 0) {
            string name;
            try { monikers[0].GetDisplayName(ctx, null, out name); } catch { continue; }
            if (name != null && name.EndsWith("VisualStudio.DTE.17.0:" + pid, StringComparison.OrdinalIgnoreCase)) {
                object obj;
                if (rot.GetObject(monikers[0], out obj) == 0) return obj;
            }
        }
        return null;
    }
}
"@
$dte = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    try { $dte = [RotHelper]::GetDteByPid($vs.Id) } catch { $dte = $null }
    if ($dte) { break }
    Start-Sleep -Seconds 3
}
if (-not $dte) { Write-Host "[FAIL] DTE not attached to PID $($vs.Id)"; try { Stop-Process -Id $vs.Id -Force } catch {}; exit 1 }
Write-Host "[info] DTE attached (own instance), waiting for ZooVS ready..."
Write-Host "[info] DTE ok (own instance), waiting for ZooVS ready..."
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $zl = "$env:LOCALAPPDATA\ZooVS\log\zoovs.log"
    if ((Test-Path $zl) -and (Select-String -Path $zl -Pattern "webview 已加载" -Quiet)) { break }
    Start-Sleep -Seconds 3
}
Start-Sleep -Seconds 5
Write-Host "[info] opening Program.cs and simulating typing..."
$null = $dte.ItemOperations.OpenFile($program)
Start-Sleep -Seconds 3
$doc = $dte.ActiveDocument
if (-not $doc) { Write-Host "[FAIL] no ActiveDocument"; try { Stop-Process -Id $vs.Id -Force } catch {}; exit 1 }
$sel = $doc.Selection
# 在 Main 里第 15 行(total += Compute(i);)后模拟输入触发补全
$sel.GotoLine(15, $true)
Start-Sleep -Milliseconds 300
$sel.EndOfLine($false)
$sel.Insert(" int z =")
Write-Host "[info] typed, waiting for debounce+HTTP..."
Start-Sleep -Seconds 12
# 再输入一次(带标识符前缀,更易出建议)
$sel.EndOfLine($false)
$sel.Insert(" Math.")
Start-Sleep -Seconds 12

# ===== 截图验证灰字渲染位置(必须发生在退出之前,且只前台本实例) =====
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    // PrintWindow + PW_RENDERFULLCONTENT(2):即使窗口被完全遮挡/在后台也能渲染内容,
    // 不依赖前台权限与 z-order(VS 实测开在左侧副屏且被终端覆盖,CopyFromScreen 抓不到)
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
try {
    $proc = Get-Process -Id $vs.Id -ErrorAction Stop
    if ($proc.MainWindowHandle -ne 0) {
        $h = $proc.MainWindowHandle
        $r = New-Object Win32+RECT
        [Win32]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
        Write-Host ("[info] VS window rect: " + $r.Left + "," + $r.Top + " ${w}x${ht}")
        if ($w -gt 0 -and $ht -gt 0) {
            $bmp = New-Object System.Drawing.Bitmap($w, $ht)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $hdc = $g.GetHdc()
            $ok = [Win32]::PrintWindow($h, $hdc, 2)
            $g.ReleaseHdc($hdc)
            $bmp.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Host ("[info] PrintWindow=" + $ok + " screenshot saved: " + $shotPath)
        } else { Write-Host "[WARN] window rect invalid" }
    } else { Write-Host "[WARN] no MainWindowHandle" }
} catch { Write-Host ("[WARN] screenshot failed: " + $_.Exception.Message) }

# ===== 清理:不保存修改,只关本实例 =====
try { $doc.Close(2) | Out-Null } catch {}   # vsSaveChangesNo = 2
Write-Host "===== inline-completion.log ====="
if (Test-Path $logFile) { Get-Content $logFile -Encoding UTF8 | Select-Object -Last 40 } else { Write-Host "(log not created)" }
try { $dte.Quit() } catch {}
Start-Sleep -Seconds 5
try { Stop-Process -Id $vs.Id -Force -ErrorAction SilentlyContinue } catch {}
