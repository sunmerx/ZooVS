using System.Reflection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Hosting;

// MCP stdio 服务严禁向 stdout 打印任何非协议内容
var logToStderr = (string m) => Console.Error.WriteLine($"[zoo-mcp] {m}");

// 必须在任何 Microsoft.Build 程序集加载前定位 MSBuild(取 VS 自带的,保证与用户项目兼容)
// MSBuildLocator 的 VSSetup 枚举在部分环境下失败(实测只列 dotnet SDK),
// 改为直接探测 VS 的 MSBuild.exe 路径并注册 —— 与用户已安装 VS 的 MSBuild/Roslyn 完全一致。
Microsoft.Build.Locator.VisualStudioInstance? instance = null;
try
{
    var candidates = new List<string>();
    var vsInstallDir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
    if (!string.IsNullOrEmpty(vsInstallDir))
        candidates.Add(Path.Combine(vsInstallDir, "MSBuild", "Current", "Bin"));
    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    foreach (var sku in new[] { "Enterprise", "Professional", "Community", "BuildTools" })
    {
        candidates.Add(Path.Combine(pf, "Microsoft Visual Studio", "2022", sku, "MSBuild", "Current", "Bin"));
        candidates.Add(Path.Combine(pf, "Microsoft Visual Studio", "18", sku, "MSBuild", "Current", "Bin"));
    }
    foreach (var dir in candidates)
    {
        if (File.Exists(Path.Combine(dir, "MSBuild.exe")))
        {
            Microsoft.Build.Locator.MSBuildLocator.RegisterMSBuildPath(dir);
            instance = MSBuildLocator.QueryVisualStudioInstances().First();
            break;
        }
    }
    if (instance == null)
    {
        // 兜底:取最高版本的 dotnet SDK 实例
        instance = MSBuildLocator.QueryVisualStudioInstances()
            .Where(i => i.DiscoveryType == Microsoft.Build.Locator.DiscoveryType.DotNetSdk)
            .OrderByDescending(i => i.Version)
            .FirstOrDefault();
    }
}
catch (Exception ex)
{
    logToStderr("MSBuild 实例发现失败: " + ex.Message);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new StderrLoggerProvider(logToStderr));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// 简单 stderr 日志提供器(stdio 协议占用 stdout,日志一律走 stderr)
internal sealed class StderrLoggerProvider(Action<string> write) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName, write);
    public void Dispose() { }
}

internal sealed class StderrLogger(string category, Action<string> write) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        => write($"[{category}] {fmt(state, ex)}{(ex != null ? " :: " + ex.Message : "")}");
}

public sealed class ZooMcpServerMarker { }
