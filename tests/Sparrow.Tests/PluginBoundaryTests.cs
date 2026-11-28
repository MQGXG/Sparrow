using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 守住「工具都是插件」的结构边界。
///
/// 三条都不能靠自觉：
///   1. 宿主源码里不出现具体工具 ID（否则就是变相内置）
///   2. 宿主不引用插件命名空间（只允许 Sparrow.Plugin.Sdk）
///   3. 插件项目不引用宿主（否则插件化退化成编译期内置）
/// </summary>
public sealed class PluginBoundaryTests
{
    /// <summary>形如 "sparrow.ocr.image" 的工具 ID 字面量；排除 sparrow.plugin.* / sparrow.sdk.*。</summary>
    private static readonly Regex ToolIdPattern = new(
        @"""(?<id>sparrow\.(?!plugin\.|sdk\.)[a-z0-9-]+\.[a-z0-9-]+)""",
        RegexOptions.Compiled);

    /// <summary>
    /// 插件程序集命名空间，<c>Sparrow.Plugin.Sdk</c> 与 <c>Sparrow.Plugin.Ui</c>
    /// 是宿主拥有的契约，其他 <c>Sparrow.Plugin.*</c> 都视为具体插件。
    /// </summary>
    private static readonly Regex PluginNamespacePattern = new(
        @"\bSparrow\.Plugin\.(?!Sdk\b|Ui\b)[A-Za-z]",
        RegexOptions.Compiled);

    /// <summary>宿主直接按字面量写插件参数键（<c>parameters["translate"]</c>）。</summary>
    private static readonly Regex ParameterKeyPattern = new(
        @"parameters\[""(?<key>[^""]+)""\]",
        RegexOptions.Compiled);

    [Fact]
    public void 宿主不得硬编码具体工具ID()
    {
        var hits = ScanHost()
            .Where(hit => ToolIdPattern.IsMatch(StripLineComment(hit.Line)))
            .Select(hit => $"{hit.File}:{hit.Number}  {hit.Line.Trim()}")
            .ToList();

        hits.ShouldBeEmpty(
            "宿主里出现具体工具 ID 会让插件化退化成内置，请改用 ToolDescriptor 或通用契约："
            + Environment.NewLine
            + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void 宿主不得引用插件里的命名空间()
    {
        var hits = ScanHost()
            .Where(hit => PluginNamespacePattern.IsMatch(StripLineComment(hit.Line)))
            .Select(hit => $"{hit.File}:{hit.Number}  {hit.Line.Trim()}")
            .ToList();

        hits.ShouldBeEmpty(
            "宿主只能依赖 Sparrow.Plugin.Sdk / Sparrow.Plugin.Ui 契约；引用具体插件会把插件变成编译期内置："
            + Environment.NewLine
            + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void 宿主不得散落插件参数键()
    {
        var hits = ScanHost()
            .Where(hit => ParameterKeyPattern.IsMatch(StripLineComment(hit.Line)))
            .Select(hit => $"{hit.File}:{hit.Number}  {hit.Line.Trim()}")
            .ToList();

        hits.ShouldBeEmpty(
            "宿主直接写插件参数键会让参数契约散落；请集中到 CaptureOcrParameters 常量："
            + Environment.NewLine
            + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void 插件项目不得引用宿主()
    {
        var pluginsRoot = Path.Combine(RepoRoot(), "plugins");
        var separator = Path.DirectorySeparatorChar;

        var offenders = Directory
            .EnumerateFiles(pluginsRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}bin{separator}")
                        && !file.Contains($"{separator}obj{separator}"))
            .Where(file => File.ReadAllText(file).Contains("Sparrow.App", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepoRoot(), file))
            .ToList();

        offenders.ShouldBeEmpty(
            "插件只能引用 Sparrow.Plugin.Sdk，不能引用 Sparrow.App：" + string.Join(", ", offenders));
    }

    /// <summary>扫描宿主源码（cs / axaml），跳过 obj 与 bin。</summary>
    private static IEnumerable<(string File, int Number, string Line)> ScanHost()
    {
        var hostRoot = Path.Combine(RepoRoot(), "src", "Sparrow.App");
        var separator = Path.DirectorySeparatorChar;

        foreach (var file in Directory.EnumerateFiles(hostRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.Contains($"{separator}bin{separator}")
                || file.Contains($"{separator}obj{separator}"))
            {
                continue;
            }

            var relative = Path.GetRelativePath(RepoRoot(), file);
            var lines = File.ReadAllLines(file);

            for (var index = 0; index < lines.Length; index++)
                yield return (relative, index + 1, lines[index]);
        }
    }

    /// <summary>去掉行注释，避免文档示例里的工具 ID 被误判。</summary>
    private static string StripLineComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index] : line;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sparrow.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根：从测试目录向上没有 Sparrow.slnx");
    }
}
