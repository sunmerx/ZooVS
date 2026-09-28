# 行内补全端到端实测:开 VS -> 打开 Program.cs -> 模拟输入 -> 截图(退出前) -> 读日志
# 关键约束:多 devenv 实例并存时,一切操作(前台/截图/退出)只针对本脚本启动的 PID,
# 不触碰用户自己打开的 VS。
# 用法: -VsVersion 2019|2022|2026 (默认 2022)
param([int]$VsVersion = 2022)
$ErrorActionPreference = "Continue"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$program = Join-Path $projDir "Program.cs"
$logFile = "$env:LOCALAPPDATA\ZooVS\log\inline-completion.log"
$shotPath = "E:\code\cline-vsex\zoo-vsix\test\inline_shot_$VsVersion.png"
Remove-Item $logFile -ErrorAction SilentlyContinue

$vsPaths = @{
    2019 = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.exe"
    2022 = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"
    2026 = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\devenv.exe"
}
$dteNames = @{ 2019 = "VisualStudio.DTE.16.0"; 2022 = "VisualStudio.DTE.17.0"; 2026 = "VisualStudio.DTE.18.0" }
if (-not $vsPaths.ContainsKey($VsVersion)) { Write-Host "[FAIL] unknown version $VsVersion"; exit 1 }
$vs = Start-Process $vsPaths[$VsVersion] -ArgumentList "`"$($projDir)\DebugTarget.csproj`"" -PassThru
Write-Host ("[info] spawned devenv PID=" + $vs.Id)

$dteName = $dteNames[$VsVersion]
# DTE 按 ROT 显示名精确连接到本实例(枚举 ROT,匹配 !<dteName>:<pid>)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public class RotHelper {
    [DllImport("ole32.dll")] public static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] public static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    public static System.Collections.Generic.List<string> DumpDteNames() {
        var names = new System.Collections.Generic.List<string>();
        IRunningObjectTable rot; IEnumMoniker enumMon;
        if (GetRunningObjectTable(0, out rot) != 0) return names;
        rot.EnumRunning(out enumMon);
        var monikers = new IMoniker[1]; IntPtr fetched = IntPtr.Zero; IBindCtx ctx;
        if (CreateBindCtx(0, out ctx) != 0) return names;
        while (enumMon.Next(1, monikers, fetched) == 0) {
            string name;
            try { monikers[0].GetDisplayName(ctx, null, out name); } catch { continue; }
            if (name != null && name.IndexOf("DTE", StringComparison.OrdinalIgnoreCase) >= 0) names.Add(name);
        }
        return names;
    }
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
            if (name != null && name.EndsWith($dteName + ":" + pid, StringComparison.OrdinalIgnoreCase)) {
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
    # VS2019 实测不注册按 PID 的 ROT 名,回退版本级 GetActiveObject(串行测试下无歧义)
    if (-not $dte) {
        try { $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($dteNames[$VsVersion]) } catch { $dte = $null }
        if ($dte -and $dte.VSVersion) { Write-Host ("[info] fallback GetActiveObject DTE " + $dte.VSVersion.ToString(2)) }
    }
    if ($dte) { break }
    Start-Sleep -Seconds 3
}
if (-not $dte) {
    Write-Host "[FAIL] DTE not attached to PID $($vs.Id); ROT DTE names:"
    [RotHelper]::DumpDteNames() | ForEach-Object { Write-Host ("  ROT: " + $_) }
    try { Stop-Process -Id $vs.Id -Force } catch {}
    exit 1
}
Write-Host "[info] DTE attached (own instance), waiting for ZooVS ready..."
Write-Host "[info] DTE ok (own instance), waiting for ZooVS ready..."
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $zl = "$env:LOCALAPPDATA\ZooVS\log\zoovs.log"
    if ((Test-Path $zl) -and (Select-String -Path $zl -Pattern "webview 已加载" -Quiet)) { break }
    Start-Sleep -Seconds 3
}
Start-Sleep -Seconds 5
# 打开 ZooVS Chat:枚举含 ZooVS 的命令并执行(全新 hive 包未加载,命令注册表来自 pkgdef)
$zooCmd = $null
try {
    foreach ($c in $dte.Commands) {
        if ($c.Name -and $c.Name -match "ZooVS") { $zooCmd = $c; break }
    }
} catch { }
if ($zooCmd) {
    Write-Host ("[info] executing command: " + $zooCmd.Name)
    $dte.ExecuteCommand($zooCmd.Name)
    Start-Sleep -Seconds 20
} else {
    Write-Host "[WARN] ZooVS command not found in DTE"
}
Write-Host "[info] opening Program.cs and simulating typing..."
$null = $dte.ItemOperations.OpenFile($program)
Start-Sleep -Seconds 3
# VS2026 实测 Documents/ActiveDocument 集合异常(恒空):优先 ActiveDocument,
# 失败则回退 Window.Selection(文档窗口自带 Selection,不经过 Documents 集合)
$sel = $null
$deadline2 = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline2) {
    try { if ($dte.ActiveDocument) { $sel = $dte.ActiveDocument.Selection; break } } catch { }
    try {
        foreach ($w in $dte.Windows) {
            if ($w.Caption -match "Program\.cs" -and $w.Object -is [System.__ComObject]) {
                try { $sel = $w.Selection; if ($sel) { break } } catch { }
            }
            if ($sel) { break }
        }
    } catch { }
    if ($sel) { break }
    Start-Sleep -Seconds 3
}
if (-not $sel) {
    # VS2026:DTE 对象僵尸(MainWindow/Windows/ActiveDocument 全空),改 UI 键入
    Write-Host "[info] DTE selection unavailable, falling back to UI typing"
    $wsh = New-Object -ComObject WScript.Shell
    if (-not $wsh.AppActivate($vs.Id)) { Write-Host "[FAIL] AppActivate failed"; try { Stop-Process -Id $vs.Id -Force } catch {}; exit 1 }
    Start-Sleep -Seconds 3
    # 文件尾部(闭合大括号后)键入,非空行前缀可触发防抖
    $wsh.SendKeys("^{END}")
    Start-Sleep -Milliseconds 800
    $wsh.SendKeys("{ENTER}")
    Start-Sleep -Milliseconds 500
    $wsh.SendKeys(" int z =")
    $uiTyping = $true
    Write-Host "[info] UI typing done, waiting for debounce+HTTP..."
    Start-Sleep -Seconds 14
    $wsh.SendKeys(" Math.")
    Start-Sleep -Seconds 14
} else {
    Write-Host "[info] selection acquired"
}
# (UI 键入路径已等待,下面 DTE 路径继续原流程)
# 在 Main 里第 15 行(total += Compute(i);)后模拟输入触发补全
if (-not $uiTyping) {
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
}

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
