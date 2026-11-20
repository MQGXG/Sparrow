using Shouldly;
using Sparrow.App.Services;
using Xunit;

namespace Sparrow.Tests;

/// <summary>文件类型图标：走注册表关联，按扩展名缓存（参考 QcClip 用的 file_icon_provider）。</summary>
public sealed class FileIconProviderTests
{
    [Fact]
    public void 空路径与不存在的文件取不到图标()
    {
        FileIconProvider.TryGet(null).ShouldBeNull();
        FileIconProvider.TryGet("   ").ShouldBeNull();
        FileIconProvider.TryGet(@"C:\definitely-not-here\missing\file.txt").ShouldBeNull();
    }

    [Fact]
    public void 存在的文件能取到类型图标()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = System.IO.Path.Combine(directory, "笔记.txt");
            File.WriteAllText(path, "内容");

            var icon = FileIconProvider.TryGet(path);

            icon.ShouldNotBeNull("Windows 上 .txt 一定有关联图标");
            icon!.Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }

    [Fact]
    public void 同扩展名的文件复用同一份图标()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var first = System.IO.Path.Combine(directory, "a.txt");
            var second = System.IO.Path.Combine(directory, "b.txt");
            File.WriteAllText(first, "x");
            File.WriteAllText(second, "x");

            var firstIcon = FileIconProvider.TryGet(first);
            if (firstIcon is null)
                return; // 环境取不到图标时跳过，缓存这条只在能取到时才有意义

            var secondIcon = FileIconProvider.TryGet(second);

            ReferenceEquals(firstIcon, secondIcon)
                .ShouldBeTrue("同扩展名的图标来自注册表关联，必须只提取一次");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 临时目录留给系统清理
            }
        }
    }
}
