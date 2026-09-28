# VS2026 DTE 探针:OpenFile 后 Documents 集合状态
param([int]$VsVersion = 2026)
$ErrorActionPreference = "Continue"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$program = Join-Path $projDir "Program.cs"
$vsPaths = @{
    2019 = "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\devenv.exe"
    2022 = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"
    2026 = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\devenv.exe"
}
$dteNames = @{ 2019 = "VisualStudio.DTE.16.0"; 2022 = "VisualStudio.DTE.17.0"; 2026 = "VisualStudio.DTE.18.0" }
$vs = Start-Process $vsPaths[$VsVersion] -ArgumentList "`"$projDir\DebugTarget.csproj`"" -PassThru
Write-Host ("[probe] PID=" + $vs.Id)
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
Start-Sleep -Seconds 60
$dte = $null
Write-Host "[probe] ROT DTE names:"
[RotHelper]::DumpDteNames() | ForEach-Object { Write-Host ("  ROT: " + $_) }
try { $dte = [RotHelper]::GetDteByPid($vs.Id) } catch { $dte = $null }
if (-not $dte) {
    try { $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($dteNames[$VsVersion]) } catch { }
}
if (-not $dte) { Write-Host "[probe] no DTE"; try { Stop-Process -Id $vs.Id -Force } catch {}; exit 1 }
Write-Host ("[probe] DTE version: " + $dte.Version)
try {
    Write-Host ("[probe] before OpenFile: Documents.Count=" + $dte.Documents.Count)
} catch { Write-Host ("[probe] Documents.Count error: " + $_.Exception.Message) }
try {
    $null = $dte.ItemOperations.OpenFile($program, "{00000000-0000-0000-0000-000000000000}")
    Write-Host "[probe] OpenFile ok"
} catch {
    Write-Host ("[probe] OpenFile error: " + $_.Exception.Message)
}
Start-Sleep -Seconds 15
try { Write-Host ("[probe] after: Documents.Count=" + $dte.Documents.Count) } catch { Write-Host ("[probe] count error: " + $_.Exception.Message) }
try { Write-Host ("[probe] MainWindow=" + $dte.MainWindow.Caption) } catch { Write-Host ("[probe] MW error: " + $_.Exception.Message) }
try { Write-Host ("[probe] Windows.Count=" + $dte.Windows.Count) } catch { Write-Host ("[probe] W.Count error: " + $_.Exception.Message) }
try { foreach ($w in $dte.Windows) { Write-Host ("[probe] win: [" + $w.Caption + "] kind=" + $w.Kind + " doc=" + $(if ($w.Document) { $w.Document.Name } else { "null" })) } } catch { Write-Host ("[probe] win enum error: " + $_.Exception.Message) }
try { Write-Host ("[probe] Solution.Count=" + $dte.Solution.Projects.Count) } catch { Write-Host ("[probe] sol error: " + $_.Exception.Message) }
try {
    foreach ($d in $dte.Documents) { Write-Host ("[probe] doc: " + $d.FullName + " active=" + ($d -eq $dte.ActiveDocument)) }
} catch { Write-Host ("[probe] enum error: " + $_.Exception.Message) }
try { Write-Host ("[probe] ActiveDocument=" + $dte.ActiveDocument.FullName) } catch { Write-Host ("[probe] ActiveDocument error: " + $_.Exception.Message) }
try { $dte.Quit() } catch { }
Start-Sleep -Seconds 8
try { Stop-Process -Id $vs.Id -Force -ErrorAction SilentlyContinue } catch { }
