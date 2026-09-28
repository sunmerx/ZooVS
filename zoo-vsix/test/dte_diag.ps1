$ErrorActionPreference = "Continue"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$csproj = Join-Path $projDir "DebugTarget.csproj"
$devenv = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"

Start-Process $devenv -ArgumentList "`"$csproj`""
$dte = $null
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    try { $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject("VisualStudio.DTE.17.0") } catch { $dte = $null }
    if ($dte) { break }
    Start-Sleep -Seconds 3
}
if (-not $dte) { throw "no DTE" }
Write-Host "got DTE"
Start-Sleep -Seconds 20

Write-Host "Solution.FullName: '$($dte.Solution.FullName)'"
Write-Host "Solution.Projects.Count: $($dte.Solution.Projects.Count)"
foreach ($p in $dte.Solution.Projects) {
    Write-Host "  project: $($p.Name)"
}

$dbg = $dte.Debugger
Write-Host "Debugger type: $($dbg.GetType().FullName)"
try { Write-Host "DebugMode raw: $($dbg.DebugMode)" } catch { Write-Host "DebugMode err: $_" }
try { Write-Host "DebugMode int: $([int]$dbg.DebugMode)" } catch { Write-Host "DebugMode int err: $_" }
try { Write-Host "CurrentMode: $($dbg.CurrentMode)" } catch { Write-Host "CurrentMode err: $_" }
# Debugger2 视图
try {
    $dbg2 = $dte.Debugger
    Write-Host "DTE.Debugger2?: $($dte.Debugger2 -ne $null)"
} catch { Write-Host "Debugger2 err: $_" }
# 转到 EnvDTE100.Debugger5? 用 DTE.ActiveDebugger?
try { Write-Host "DTE.ActiveDebugger: $($dte.ActiveDebugger -ne $null)" } catch { Write-Host "ActiveDebugger err: $_" }

# 成员清单
Write-Host "--- Debugger members ---"
$dbg | Get-Member -MemberType Property,Method | ForEach-Object { Write-Host "  $($_.MemberType): $($_.Name)" }
