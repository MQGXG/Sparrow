using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 受控文件读取：只允许本次输入里的路径。
/// 这是「插件不碰文件系统」在文件类工具上的落地方式。
/// </summary>
public sealed class ScopedFileAccessTests : IDisposable
{
    private readonly string _root;

    public ScopedFileAccessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string CreateFile(string name, string content = "hello")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void 白名单内的文件_可以读取()
    {
        var path = CreateFile("a.txt", "内容");
        var access = new ScopedFileAccess([path]);

        access.CanRead(path).ShouldBeTrue();
        System.Text.Encoding.UTF8.GetString(access.ReadAllBytes(path)!).ShouldBe("内容");
    }

    [Fact]
    public void 白名单外的文件_读不到()
    {
        var allowed = CreateFile("a.txt");
        var other = CreateFile("b.txt");

        var access = new ScopedFileAccess([allowed]);

        access.CanRead(other).ShouldBeFalse();
        access.ReadAllBytes(other).ShouldBeNull();
    }

    [Fact]
    public void 路径大小写与相对写法_都能命中白名单()
    {
        var path = CreateFile("a.txt");
        var access = new ScopedFileAccess([path]);

        access.CanRead(path.ToUpperInvariant()).ShouldBeTrue();

        // 用相对路径尝试：只要规范化后落在白名单里就允许
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        access.CanRead(relative).ShouldBeTrue();
    }

    [Fact]
    public void 文件不存在_读不到而不是抛异常()
    {
        var path = Path.Combine(_root, "missing.txt");
        var access = new ScopedFileAccess([path]);

        access.CanRead(path).ShouldBeFalse();
        access.ReadAllBytes(path).ShouldBeNull();
    }

    [Fact]
    public void 非法路径_不抛异常()
    {
        var access = new ScopedFileAccess(["", "   ", "::::"]);

        Should.NotThrow(() =>
        {
            access.CanRead("::::").ShouldBeFalse();
            access.ReadAllBytes(null!).ShouldBeNull();
        });
    }

    [Fact]
    public void 白名单内的文件_可以按流读取()
    {
        var path = CreateFile("a.txt", "hello");
        var access = new ScopedFileAccess([path]);

        using var stream = access.OpenRead(path);
        stream.ShouldNotBeNull();

        using var reader = new StreamReader(stream!);
        reader.ReadToEnd().ShouldBe("hello");
    }

    [Fact]
    public void 白名单外的文件_打不开流()
    {
        var allowed = CreateFile("a.txt");
        var other = CreateFile("b.txt");

        var access = new ScopedFileAccess([allowed]);

        access.OpenRead(other).ShouldBeNull();
        access.OpenRead(Path.Combine(_root, "missing.txt")).ShouldBeNull();
    }

    [Fact]
    public void 非法路径_打不开流而不是抛异常()
    {
        var access = new ScopedFileAccess(["::::"]);

        Should.NotThrow(() => access.OpenRead("::::").ShouldBeNull());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录留给系统清理
        }
    }
}
