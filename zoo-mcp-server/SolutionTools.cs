using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace ZooMcpServer;

/// <summary>
/// Roslyn 语言功能与解决方案模型工具集(C1 + C4)。
/// </summary>
[McpServerToolType]
public static class SolutionTools
{
    private static readonly object Gate = new();
    private static MSBuildWorkspace? _workspace;
    private static Solution? _solution;
    private static string? _solutionPath;

    public static async Task<Solution> GetSolutionAsync(string solutionPath)
    {
        lock (Gate)
        {
            if (_solution != null &&
                string.Equals(_solutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
            {
                return _solution;
            }
        }

        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            Console.Error.WriteLine($"[ws:{e.Diagnostic.Kind}] {e.Diagnostic.Message}");
        var ext = Path.GetExtension(solutionPath).ToLowerInvariant();
        Solution solution;
        if (ext == ".sln" || ext == ".slnx")
        {
            solution = await workspace.OpenSolutionAsync(solutionPath);
        }
        else
        {
            var project = await workspace.OpenProjectAsync(solutionPath);
            solution = project.Solution;
        }

        lock (Gate)
        {
            _workspace = workspace;
            _solution = solution;
            _solutionPath = solutionPath;
        }
        return solution;
    }

    private static string DescribeSymbol(ISymbol symbol)
    {
        var sb = new StringBuilder();
        sb.Append(symbol.Kind).Append(' ').Append(symbol.ToDisplayString());
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc != null)
        {
            var lineSpan = loc.GetLineSpan();
            sb.Append($"  @ {loc.SourceTree?.FilePath}:{lineSpan.StartLinePosition.Line + 1}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 定位符号。支持 "文件路径:行:列"(1 基)或全名(FQN,两段式:成员 → 容器类型回退)。
    /// </summary>
    private static async Task<ISymbol?> ResolveSymbolAsync(Solution solution, string symbolRef)
    {
        var parts = symbolRef.Split(':');
        if (parts.Length >= 3)
        {
            var filePath = string.Join(':', parts.Take(parts.Length - 2));
            if (File.Exists(filePath))
            {
                var line = int.Parse(parts[^2]);
                var col = int.Parse(parts[^1]);
                var doc = solution.GetDocumentIdsWithFilePath(filePath)
                    .Select(solution.GetDocument).FirstOrDefault();
                if (doc != null)
                {
                    var model = await doc.GetSemanticModelAsync();
                    var root = await doc.GetSyntaxRootAsync();
                    var text = await doc.GetTextAsync();
                    if (model != null && root != null)
                    {
                        var textPos = text.Lines.GetPosition(
                            new Microsoft.CodeAnalysis.Text.LinePosition(
                                Math.Clamp(line - 1, 0, text.Lines.Count - 1), Math.Max(0, col - 1)));
                        var sym = model.GetEnclosingSymbol(textPos);
                        if (sym != null && !sym.IsImplicitlyDeclared) return sym;
                        var token = root.FindToken(textPos);
                        if (token.Parent != null)
                        {
                            var info = model.GetSymbolInfo(token.Parent);
                            if (info.Symbol != null) return info.Symbol;
                            if (info.CandidateSymbols.Length > 0) return info.CandidateSymbols[0];
                        }
                        return sym;
                    }
                }
            }
            return null;
        }

        // FQN 两段式
        var memberName = symbolRef.Split('.').Last();
        var containerName = symbolRef.Substring(0, symbolRef.Length - memberName.Length).TrimEnd('.');
        if (containerName.Length == 0) containerName = memberName;

        var perProject = await Task.WhenAll(
            solution.Projects.Select(pr =>
                SymbolFinder.FindDeclarationsAsync(pr, memberName, ignoreCase: false)));
        var candidates = perProject.SelectMany(x => x).ToList();

        foreach (var s in candidates)
        {
            var d = s.ToDisplayString();
            var paren = d.IndexOf('(');
            var head = paren > 0 ? d.Substring(0, paren) : d;
            if (head == symbolRef) return s;
        }
        foreach (var s in candidates)
        {
            var d = s.ToDisplayString();
            var paren = d.IndexOf('(');
            var head = paren > 0 ? d.Substring(0, paren) : d;
            if (head.EndsWith("." + symbolRef, StringComparison.Ordinal)) return s;
        }

        var containerShort = containerName.Split('.').Last();
        var containerProject = await Task.WhenAll(
            solution.Projects.Select(pr =>
                SymbolFinder.FindDeclarationsAsync(pr, containerShort, ignoreCase: false)));
        var containerMatch = containerProject.SelectMany(x => x)
            .FirstOrDefault(s =>
            {
                var d = s.ToDisplayString();
                return d == containerName || d.EndsWith("." + containerName, StringComparison.Ordinal);
            });
        return containerMatch;
    }

    [McpServerTool]
    [Description("加载 .sln/.csproj 并返回解决方案/项目模型(项目列表与文档数)。后续语言工具复用已加载的工作区。")]
    public static async Task<string> get_solution_model(
        [Description(".sln 或 .csproj 的绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var sb = new StringBuilder();
        sb.AppendLine("解决方案: " + Path.GetFileName(solution.FilePath ?? solutionPath));
        foreach (var project in solution.Projects)
        {
            sb.AppendLine($"- {project.Name} [{project.Language}] 文档 {project.Documents.Count()} 个  ({project.FilePath})");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("按名称模式搜索符号(类/方法/属性等),返回符号类型、全名与定义位置。")]
    public static async Task<string> symbol_search(
        [Description("符号名称(部分匹配,大小写不敏感)")] string pattern,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var perProject = await Task.WhenAll(
            solution.Projects.Select(pr => SymbolFinder.FindDeclarationsAsync(pr, pattern, ignoreCase: true)));
        var lines = perProject.SelectMany(x => x)
            .Where(s => s is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet })
            .Take(50)
            .Select(DescribeSymbol);
        var list = lines.ToList();
        return list.Count > 0 ? string.Join(Environment.NewLine, list) : $"未找到匹配 '{pattern}' 的符号";
    }

    [McpServerTool]
    [Description("查找一个符号的所有引用。symbolRef 支持 '文件:行:列'(1 基)或全名。")]
    public static async Task<string> find_references(
        [Description("符号引用:文件:行:列 或 全名")] string symbolRef,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var symbol = await ResolveSymbolAsync(solution, symbolRef);
        if (symbol == null) return "未定位到符号: " + symbolRef;

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var sb = new StringBuilder();
        sb.AppendLine("符号: " + DescribeSymbol(symbol));
        var count = 0;
        foreach (var r in refs)
        {
            var locations = r.Locations.ToList();
            if (SymbolEqualityComparer.Default.Equals(r.Definition, symbol) && locations.Count == 0)
                continue;
            sb.AppendLine($"  {r.Definition.Kind} {r.Definition.ToDisplayString()}: {locations.Count} 处");
            foreach (var loc in locations.Take(30))
            {
                var ls = loc.Location.GetLineSpan();
                sb.AppendLine($"    - {loc.Document.FilePath}:{ls.StartLinePosition.Line + 1}");
                count++;
            }
        }
        return count == 0 ? symbolRef + " 无引用" : sb.ToString();
    }

    [McpServerTool]
    [Description("查找接口/抽象/虚成员的所有实现。")]
    public static async Task<string> find_implementations(
        [Description("符号引用:文件:行:列 或 全名")] string symbolRef,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var symbol = await ResolveSymbolAsync(solution, symbolRef);
        if (symbol == null) return "未定位到符号: " + symbolRef;

        var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution);
        var lines = implementations.Select(DescribeSymbol).ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : symbolRef + " 无实现(可能是具体成员)";
    }

    [McpServerTool]
    [Description("查找方法的调用关系:direction=callers(谁调用了它,按调用方方法聚合)或 callees(它调用了哪些方法/构造器)。")]
    public static async Task<string> find_calls(
        [Description("方向:callers 或 callees")] string direction,
        [Description("符号引用:文件:行:列 或 全名")] string symbolRef,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var symbol = await ResolveSymbolAsync(solution, symbolRef);
        if (symbol == null) return "未定位到符号: " + symbolRef;

        var sb = new StringBuilder();

        if (string.Equals(direction, "callees", StringComparison.OrdinalIgnoreCase))
        {
            // 声明语法内所有调用点 → 解析被调符号(去重)
            var callees = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
            {
                var doc = solution.GetDocument(loc.SourceTree);
                if (doc == null) continue;
                var model = await doc.GetSemanticModelAsync();
                var root = await doc.GetSyntaxRootAsync();
                if (model == null || root == null) continue;

                var declaration = root.FindNode(loc.SourceSpan);
                foreach (var inv in declaration.DescendantNodes()
                             .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    var info = model.GetSymbolInfo(inv);
                    var target = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (target?.OriginalDefinition is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol)
                    {
                        callees.Add(target.OriginalDefinition.ToDisplayString());
                    }
                    if (callees.Count >= 60) break;
                }
                foreach (var ctor in declaration.DescendantNodes()
                             .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>())
                {
                    var info = model.GetSymbolInfo(ctor);
                    if (info.Symbol is IMethodSymbol ctorMethod)
                    {
                        callees.Add("new " + ctorMethod.ContainingType.ToDisplayString());
                    }
                    if (callees.Count >= 60) break;
                }
            }
            sb.AppendLine($"callees of {symbol.ToDisplayString()} ({callees.Count}):");
            foreach (var c in callees.Take(50)) sb.AppendLine("- " + c);
            if (callees.Count == 0) sb.AppendLine("(无调用点,可能不是方法或为空实现)");
            return sb.ToString();
        }

        // callers:全部引用位置 → 聚合到所属方法
        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var callers = new Dictionary<string, int>();
        foreach (var rs in refs)
        {
            foreach (var rl in rs.Locations)
            {
                var model = await rl.Document.GetSemanticModelAsync();
                if (model == null) continue;
                var caller = model.GetEnclosingSymbol(rl.Location.SourceSpan.Start);
                var target = caller ?? symbol;
                // 跳过自身声明与属性访问器噪声
                if (SymbolEqualityComparer.Default.Equals(target, symbol)) continue;
                if (target is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }) continue;
                var key = target.ToDisplayString();
                callers[key] = callers.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }
        sb.AppendLine($"callers of {symbol.ToDisplayString()} ({callers.Count}):");
        foreach (var kv in callers.OrderByDescending(kv => kv.Value).Take(50))
        {
            sb.AppendLine($"- {kv.Key}  (调用 {kv.Value} 次)");
        }
        if (callers.Count == 0) sb.AppendLine("(未找到调用方)");
        return sb.ToString();
    }

    [McpServerTool]
    [Description("类型继承视图:基类链、接口列表与派生类/实现类(接口自动查实现)。")]
    public static async Task<string> hierarchy(
        [Description("类型名或 文件:行:列")] string typeRef,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var typeSymbol = await ResolveSymbolAsync(solution, typeRef) as ITypeSymbol;
        if (typeSymbol == null) return "未定位到类型: " + typeRef;
        if (typeSymbol is not INamedTypeSymbol symbol)
            return typeRef + " 不是命名类型(类型参数/数组等不支持继承视图)";

        var sb = new StringBuilder();
        var chain = new List<string>();
        var baseType = symbol.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            chain.Add(baseType.ToDisplayString());
            baseType = baseType.BaseType;
        }
        sb.AppendLine("基类链: " + (chain.Count > 0 ? string.Join(" <- ", chain) : "(直接继承 object)"));
        sb.AppendLine("接口: " + string.Join(", ", symbol.AllInterfaces.Select(i => i.ToDisplayString())));

        var lines = new List<string>();
        if (symbol.TypeKind == TypeKind.Interface)
        {
            var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution);
            foreach (var d in impls.OfType<INamedTypeSymbol>())
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                lines.Add(d.ToDisplayString() + "  @ " + (loc?.SourceTree?.FilePath ?? "?"));
            }
            sb.AppendLine(lines.Count > 0
                ? "实现类:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines)
                : "实现类: 无");
        }
        else
        {
            var derived = await SymbolFinder.FindDerivedClassesAsync(symbol, solution);
            foreach (var d in derived)
            {
                var loc = d.Locations.FirstOrDefault(l => l.IsInSource);
                lines.Add(d.ToDisplayString() + "  @ " + (loc?.SourceTree?.FilePath ?? "?"));
            }
            sb.AppendLine(lines.Count > 0
                ? "派生类:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines)
                : "派生类: 无");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("列出引用了指定符号的文档(按引用数排序),快速圈定相关文件。")]
    public static async Task<string> find_referencing_files(
        [Description("符号引用:文件:行:列 或 全名")] string symbolRef,
        [Description(".sln/.csproj 绝对路径")] string solutionPath)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var symbol = await ResolveSymbolAsync(solution, symbolRef);
        if (symbol == null) return "未定位到符号: " + symbolRef;

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var byDoc = refs.SelectMany(r => r.Locations)
            .GroupBy(l => l.Document.FilePath)
            .Select(g => (path: g.Key, count: g.Count()))
            .OrderByDescending(x => x.count);
        var lines = byDoc.Select(x => $"{x.count,4}  {x.path}").ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "无引用文档";
    }
}
