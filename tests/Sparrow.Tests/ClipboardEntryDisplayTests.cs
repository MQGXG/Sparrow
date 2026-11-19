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
    public void 文件条目显示文件名与剩余数量()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var viewModel = Create(new ClipboardHistoryEntry
            {
                Id = "files",
                Kind = ClipboardEntryKind.Files,
                Preview = "2 个文件",
                Paths = [@"C:\a\报告.docx", @"C:\a\截图.png"],
                CreatedAt = DateTimeOffset.Now
            }, directory);

            viewModel.IsFileSet.ShouldBeTrue();
            viewModel.FileTitle.ShouldBe("报告.docx");
            viewModel.FileSubtitle.ShouldBe("+1 个文件");
            viewModel.HasFileSubtitle.ShouldBeTrue();
            viewModel.ShowPlainPreview.ShouldBeFalse();
            viewModel.ShowTagLine.ShouldBeFalse();
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

