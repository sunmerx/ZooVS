# -*- coding: utf-8 -*-
# 参数化 inline_e2e.ps1:支持 2019/2022/2026 三个 VS 版本(路径/DTE 名/截图名)
import io

p = r"E:\code\cline-vsex\zoo-vsix\test\inline_e2e.ps1"
s = io.open(p, encoding='utf-8-sig').read()

# 1) 头部:param + 版本映射 + 替换硬编码 devenv 路径
old_head = '''# 行内补全端到端实测:开 VS -> 打开 Program.cs -> 模拟输入 -> 截图(退出前) -> 读日志
# 关键约束:多 devenv 实例并存时,一切操作(前台/截图/DTE/退出)只针对本脚本启动的 PID,
# 不触碰用户自己打开的 VS。
$ErrorActionPreference = "Continue"'''
new_head = '''# 行内补全端到端实测:开 VS -> 打开 Program.cs -> 模拟输入 -> 截图(退出前) -> 读日志
# 关键约束:多 devenv 实例并存时,一切操作(前台/截图/退出)只针对本脚本启动的 PID,
# 不触碰用户自己打开的 VS。
# 用法: -VsVersion 2019|2022|2026 (默认 2022)
param([int]$VsVersion = 2022)
$ErrorActionPreference = "Continue"'''
assert old_head in s, "head anchor"
s = s.replace(old_head, new_head, 1)

old_start = '''$vs = Start-Process "C:\\Program Files\\Microsoft Visual Studio\\2022\\Enterprise\\Common7\\IDE\\devenv.exe" -ArgumentList "`"$($projDir)\\DebugTarget.csproj`"" -PassThru'''
new_start = '''$vsPaths = @{
    2019 = "C:\\Program Files (x86)\\Microsoft Visual Studio\\2019\\Enterprise\\Common7\\IDE\\devenv.exe"
    2022 = "C:\\Program Files\\Microsoft Visual Studio\\2022\\Enterprise\\Common7\\IDE\\devenv.exe"
    2026 = "C:\\Program Files\\Microsoft Visual Studio\\18\\Enterprise\\Common7\\IDE\\devenv.exe"
}
$dteNames = @{ 2019 = "VisualStudio.DTE.16.0"; 2022 = "VisualStudio.DTE.17.0"; 2026 = "VisualStudio.DTE.18.0" }
if (-not $vsPaths.ContainsKey($VsVersion)) { Write-Host "[FAIL] unknown version $VsVersion"; exit 1 }
$vs = Start-Process $vsPaths[$VsVersion] -ArgumentList "`"$($projDir)\\DebugTarget.csproj`"" -PassThru'''
assert old_start in s, "start anchor"
s = s.replace(old_start, new_start, 1)

# 2) 截图路径按版本区分
s = s.replace('$shotPath = "E:\\code\\cline-vsex\\zoo-vsix\\test\\inline_shot.png"',
              '$shotPath = "E:\\code\\cline-vsex\\zoo-vsix\\test\\inline_shot_$VsVersion.png"', 1)

# 3) ROT 匹配与注释用变量
s = s.replace('# DTE 按 ROT 显示名精确连接到本实例(枚举 ROT,匹配 !VisualStudio.DTE.17.0:<pid>)',
              '$dteName = $dteNames[$VsVersion]\n# DTE 按 ROT 显示名精确连接到本实例(枚举 ROT,匹配 !<dteName>:<pid>)', 1)
s = s.replace('name.EndsWith("VisualStudio.DTE.17.0:" + pid, StringComparison.OrdinalIgnoreCase)',
              'name.EndsWith($dteName + ":" + pid, StringComparison.OrdinalIgnoreCase)', 1)

io.open(p, 'w', encoding='utf-8-sig').write(s)
print("parameterized ok")
