using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ZooMcpServer;

/// <summary>编译工具:用 Visual Studio 自带 MSBuild 构建并解析错误/警告。</summary>
[McpServerToolType]
public static class BuildTools
{
    [McpServerTool]
    [Description("构建 .sln 或 .csproj(使用 Visual Studio 的 MSBuild),返回错误与警告列表。")]
    public static async Task<string> build(
        [Description(".sln 或 .csproj 绝对路径")] string solutionPath,
        [Description("构建配置,默认 Debug")] string configuration = "Debug")
    {
        var msbuild = LocateMsBuildExe();
        if (msbuild == null)
        {
            return "未找到 MSBuild.exe。请确认已安装 Visual Studio(含 C# 工作负载)。";
        }

        // configuration 直接拼进 MSBuild 命令行,做白名单消毒防参数注入
        var safeConfiguration = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(safeConfiguration, @"^[A-Za-z0-9_\-\. ]{1,40}$"))
        {
            return $"非法的构建配置名: {configuration}";
        }

        var psi = new ProcessStartInfo
        {
            FileName = msbuild,
            Arguments = $"\"{solutionPath}\" /p:Configuration=\"{safeConfiguration}\" /m /nologo /v:m /restore /clp:Summary;ErrorsOnly",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"MSBuild 退出码: {process.ExitCode}({Path.GetFileName(solutionPath)}, {configuration})");

        // 解析 MSBuild 标准诊断行: file(line,col): error CSxxxx: message
        var diagRegex = new Regex(@"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>\w+):\s+(?<msg>.*)$",
            RegexOptions.Multiline);
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (Match m in diagRegex.Matches(output))
        {
            var sev = m.Groups["sev"].Value.ToUpperInvariant();
            var code = m.Groups["code"].Value;
            var file = m.Groups["file"].Value;
            var ln = m.Groups["line"].Value;
            var col = m.Groups["col"].Value;
            var msg = m.Groups["msg"].Value;
            var line = sev + " " + code + " " + file + ":" + ln + "," + col + " — " + msg;
            if (m.Groups["sev"].Value == "error") errors.Add(line); else warnings.Add(line);
        }

        sb.AppendLine($"错误 {errors.Count} 个,警告 {warnings.Count} 个");
        foreach (var e in errors.Take(50)) sb.AppendLine("  " + e);
        foreach (var w in warnings.Take(20)) sb.AppendLine("  " + w);
        return sb.ToString();
    }

    private static string? LocateMsBuildExe()
    {
        // VSINSTALLDIR 优先(server 由 VS 环境启动时)
        var candidates = new List<string>();
        var vsInstall = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrEmpty(vsInstall))
        {
            candidates.Add(Path.Combine(vsInstall, "MSBuild", "Current", "Bin", "MSBuild.exe"));
        }
        // 常见默认路径
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"));
        candidates.Add(Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Professional", "MSBuild", "Current", "Bin", "MSBuild.exe"));
        candidates.Add(Path.Combine(programFiles, "Microsoft Visual Studio", "2022", "Enterprise", "MSBuild", "Current", "Bin", "MSBuild.exe"));
        candidates.Add(Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Enterprise", "MSBuild", "Current", "Bin", "MSBuild.exe"));
        candidates.Add(Path.Combine(programFiles, "Microsoft Visual Studio", "18", "Community", "MSBuild", "Current", "Bin", "MSBuild.exe"));
        // dotnet build 兜底
        candidates.Add("dotnet");

        return candidates.FirstOrDefault(c =>
            c == "dotnet" || File.Exists(c));
    }
}

/// <summary>补全查询工具(Roslyn CompletionService)。</summary>
[McpServerToolType]
public static class CompletionTools
{
    [McpServerTool]
    [Description("查询某文件指定位置的补全候选(类型成员/关键字/扩展方法等),供 agent 判断可用的语言元素。")]
    public static async Task<string> completion_at(
        [Description("文件绝对路径")] string filePath,
        [Description("行号(1 基)")] int line,
        [Description("列号(1 基)")] int column,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await SolutionTools.GetSolutionAsync(solutionPath);
        var doc = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (doc == null) return $"文件不在工作区中: {filePath}";

        var text = await doc.GetTextAsync();
        var position = text.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(line - 1, column - 1));
        var service = Microsoft.CodeAnalysis.Completion.CompletionService.GetService(doc);
        if (service == null) return "此文档语言不支持补全服务";

        var completions = await service.GetCompletionsAsync(doc, position);
        if (completions == null || completions.ItemsList.Count == 0) return "无补全候选";

        var items = completions.ItemsList
            .GroupBy(i => i.DisplayText)
            .Select(g => g.First())
            .Take(60)
            .Select(i => i.DisplayText + (i.Tags.Contains("Method") ? "()" : ""))
            .Distinct();
        return string.Join("\n", items);
    }
}
