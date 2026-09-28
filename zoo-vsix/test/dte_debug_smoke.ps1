# DTE 调试自动化冒烟(走真实 devenv 实例):
# devenv 打开测试工程 -> ROT GetActiveObject 拿 DTE -> 设断点 -> Debug() -> 等断点
# -> 检查局部变量/表达式/调用栈 -> StepOver -> TerminateAll -> Quit
$ErrorActionPreference = "Stop"
$projDir = "E:\code\cline-vsex\zoo-vsix\test\debug-target"
$csproj = Join-Path $projDir "DebugTarget.csproj"
$program = Join-Path $projDir "Program.cs"
$devenv = "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"

$bpLine = 0
$lines = Get-Content $program
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "int doubled") { $bpLine = $i + 1; break }
}
Write-Host "[info] breakpoint line = $bpLine"

Start-Process $devenv -ArgumentList "`"$csproj`""
$dte = $null
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    try { $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject("VisualStudio.DTE.17.0") } catch { $dte = $null }
    if ($dte) { break }
    Start-Sleep -Seconds 3
}
if (-not $dte) { throw "DTE not found in ROT after 120s" }
Write-Host "[info] got DTE from ROT"

# 等工程加载完成(Solution 非空)
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) {
    $full = $null
    try { $full = $dte.Solution.FullName } catch { $full = $null }
    if ($full) { break }
    Start-Sleep -Seconds 2
}
try { Write-Host "[info] solution: $($dte.Solution.FullName)" } catch {}
Start-Sleep -Seconds 5

try {
    $dte.Debugger.Breakpoints.Add($null, $program, $bpLine) | Out-Null
    Write-Host "[PASS] Breakpoints.Add"
    if ($dte.Debugger.Breakpoints.Count -lt 1) { throw "breakpoint not registered" }
    Write-Host "[PASS] Breakpoints.Count = $($dte.Debugger.Breakpoints.Count)"

    $dte.Solution.SolutionBuild.Debug()
    Write-Host "[PASS] SolutionBuild.Debug()"

    $deadline = (Get-Date).AddSeconds(180)
    while ($dte.Debugger.CurrentMode -ne 2 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if ($dte.Debugger.CurrentMode -ne 2) { throw "timeout waiting break mode, mode=$($dte.Debugger.CurrentMode)" }
    Write-Host "[PASS] entered break mode"

    $frame = $dte.Debugger.CurrentStackFrame
    Write-Host "[PASS] CurrentStackFrame = $($frame.FunctionName)"
    $localsText = @()
    foreach ($e in $frame.Locals) { $localsText += "$($e.Name)=$($e.Value)" }
    Write-Host ("[PASS] Locals: " + ($localsText -join ", "))

    $expr = $dte.Debugger.GetExpression("x + 1")
    if (-not $expr.IsValidValue) { throw "GetExpression invalid" }
    Write-Host "[PASS] GetExpression('x + 1') = $($expr.Value)"

    $frames = @()
    foreach ($f in $dte.Debugger.CurrentThread.StackFrames) { $frames += $f.FunctionName }
    Write-Host ("[PASS] StackFrames: " + ($frames -join " <- "))

    $dte.Debugger.StepOver($true)
    $deadline = (Get-Date).AddSeconds(60)
    while ($dte.Debugger.CurrentMode -ne 2 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    if ($dte.Debugger.CurrentMode -ne 2) { throw "timeout after StepOver" }
    Write-Host "[PASS] StepOver -> break (frame=$($dte.Debugger.CurrentStackFrame.FunctionName))"

    foreach ($p in $dte.Debugger.DebuggedProcesses) { Write-Host "[PASS] DebuggedProcess: $($p.Name)" }

    $dte.Debugger.TerminateAll()
    $deadline = (Get-Date).AddSeconds(60)
    while ($dte.Debugger.CurrentMode -ne 1 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if ($dte.Debugger.CurrentMode -ne 1) { throw "timeout waiting design mode after TerminateAll" }
    Write-Host "[PASS] TerminateAll -> design mode"

    Write-Host "RESULT: ALL PASS"
}
finally {
    try { $dte.Quit() } catch {}
}
