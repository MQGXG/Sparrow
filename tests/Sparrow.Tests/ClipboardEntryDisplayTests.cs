using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>链接与文件条目的展示派生字段：域名、首字母、文件名。</summary>
public sealed class ClipboardEntryDisplayTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static ClipboardEntryViewModel Create(ClipboardHistoryEntry entry, string directory) =>
        new(entry, new ClipboardHistoryStore(
            System.IO.Path.Combine(directory, "clipboard.db"), new NullLog()));

    [Fact]
    public void 链接条目提取域名并去掉www前缀()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "link",
                Kind = ClipboardEntryKind.Text,
                Preview = "https://www.github.com/jimmgreen/LumaShot",
                Text = "https://www.github.com/jimmgreen/LumaShot",
                CreatedAt = DateTimeOffset.Now,
                AutoTags = [ClipboardClassifier.TagLink]
            }, directory);

            viewModel.IsLink.ShouldBeTrue();
            viewModel.LinkHost.ShouldBe("github.com");
            viewModel.LinkInitial.ShouldBe("G");
            viewModel.KindLabel.ShouldBe("链接");
            viewModel.ShowPlainPreview.ShouldBeFalse("链接有自己的布局");
            viewModel.ShowTagLine.ShouldBeFalse("类型已经是「链接」，不再重复标签");
            viewModel.CanOpen.ShouldBeTrue("链接可以用浏览器打开");
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
    public void 普通文本不是链接()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "text",
                Kind = ClipboardEntryKind.Text,
                Preview = "先解决这个吧",
                Text = "先解决这个吧",
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.IsLink.ShouldBeFalse();
            viewModel.LinkHost.ShouldBeEmpty();
            viewModel.ShowPlainPreview.ShouldBeTrue();
            viewModel.CanOpen.ShouldBeFalse("普通文本没有打开目标");
            viewModel.KindLabel.ShouldBe("文本");
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
    public void 图片条目写临时文件后用默认程序打开()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            // 1×1 的最小 PNG
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"), new NullLog());
            store.AddImage(png);

            var entry = store.Recent(1)[0];
            var viewModel = new ClipboardEntryViewModel(entry, store);

            viewModel.CanOpen.ShouldBeTrue("图片可以写临时文件后用默认程序打开");

            var target = viewModel.ResolveOpenTarget();
            target.ShouldNotBeNull();
            File.Exists(target).ShouldBeTrue("应写出临时 PNG");
            new FileInfo(target!).Length.ShouldBe(png.Length);
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
    public void 文件不存在时不能打开()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "missing",
                Kind = ClipboardEntryKind.Files,
                Preview = "1 个文件",
                Paths = [System.IO.Path.Combine(directory, "已经删掉.pdf")],
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.CanOpen.ShouldBeFalse();
            viewModel.ResolveOpenTarget().ShouldBeNull();
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
    public void 文本路径可以用默认程序打开()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var file = System.IO.Path.Combine(directory, "笔记.txt");
            File.WriteAllText(file, "内容");

            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "path-text",
                Kind = ClipboardEntryKind.Text,
                Preview = file,
                Text = file,
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.IsLink.ShouldBeFalse();
            viewModel.IsPath.ShouldBeTrue();
            viewModel.CanOpen.ShouldBeTrue("存在的文件路径可以打开");
            viewModel.ResolveOpenTarget().ShouldBe(file);
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
    public void 文本目录路径也可以打开()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "path-dir",
                Kind = ClipboardEntryKind.Text,
                Preview = directory,
                Text = directory,
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.IsPath.ShouldBeTrue();
            viewModel.CanOpen.ShouldBeTrue("存在的目录也可以用资源管理器打开");
            viewModel.ResolveOpenTarget().ShouldBe(directory);
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
    public void 文件条目显示文件名与剩余数量()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var first = System.IO.Path.Combine(directory, "报告.docx");
            var second = System.IO.Path.Combine(directory, "截图.png");
            File.WriteAllText(first, "doc");
            File.WriteAllText(second, "png");

            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "files",
                Kind = ClipboardEntryKind.Files,
                Preview = "2 个文件",
                Paths = [first, second],
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.IsFileSet.ShouldBeTrue();
            viewModel.FileTitle.ShouldBe("报告.docx");
            viewModel.FileSubtitle.ShouldBe("+1 个文件");
            viewModel.HasFileSubtitle.ShouldBeTrue();
            viewModel.ShowPlainPreview.ShouldBeFalse();
            viewModel.ShowTagLine.ShouldBeFalse();
            viewModel.CanOpen.ShouldBeTrue("存在的文件可以用默认程序打开");
            viewModel.ResolveOpenTarget().ShouldBe(first, "默认程序打开第一个文件");
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



