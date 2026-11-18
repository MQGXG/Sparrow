using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Shouldly;
using Sparrow.App.Services;
using Sparrow.Plugin.Sdk;
using Xunit;

namespace Sparrow.Tests;

/// <summary>
/// 拖出格式与临时文件命名。
/// 真实拖放需要桌面环境，这里只锁「提供了哪些格式」和「文件名是否可读」。
/// </summary>
public sealed class ClipboardDragServiceTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void 文件名_取首个非空行并去掉非法字符()
    {
        var name = ClipboardDragService.BuildTextBaseName("""

            第一行: 标题/带*非法字符?
            第二行
            """);

        name.ShouldBe("第一行_ 标题_带_非法字符_");
    }

    [Fact]
    public void 文件名_压缩空白并截断()
    {
        var name = ClipboardDragService.BuildTextBaseName("a    b\t\tc" + new string('x', 100));

        name.Length.ShouldBe(40);
        name.ShouldStartWith("a b c");
    }

    [Fact]
    public void 文件名_全空白时给默认名()
    {
        ClipboardDragService.BuildTextBaseName("   \n\t\n").ShouldBe("剪贴板文本");
    }

    [Fact]
    public async Task 文本_默认同时提供文本与文件格式()
    {
        var payload = new ClipboardPastePayload(ClipboardEntryKind.Text, "拖我出去", null, []);

        var drag = await ClipboardDragService.CreateAsync(payload, storage: null);

        drag.ShouldNotBeNull();

        var transfer = (IDataTransfer)drag!.Transfer;
        transfer.Contains(DataFormat.Text).ShouldBeTrue();
        // 没有 StorageProvider 时文件格式只能跳过，但文本仍然可用
        transfer.Contains(DataFormat.File).ShouldBeFalse();
    }

    [Fact]
    public async Task 文本_只给内联时不产生文件()
    {
        var payload = new ClipboardPastePayload(ClipboardEntryKind.Text, "只要文本", null, []);

        var drag = await ClipboardDragService.CreateAsync(
            payload,
            storage: null,
            ClipboardDragMode.InlineOnly);

        drag.ShouldNotBeNull();
        var transfer = (IDataTransfer)drag!.Transfer;
        transfer.Contains(DataFormat.Text).ShouldBeTrue();
        transfer.Contains(DataFormat.File).ShouldBeFalse();
    }

    [Fact]
    public async Task 文本_只给文件且没有存储能力时返回空()
    {
        var payload = new ClipboardPastePayload(ClipboardEntryKind.Text, "只要文件", null, []);

        (await ClipboardDragService.CreateAsync(
            payload,
            storage: null,
            ClipboardDragMode.FileOnly)).ShouldBeNull();
    }

    [AvaloniaFact]
    public async Task 图片_内联时提供位图()
    {
        var payload = new ClipboardPastePayload(ClipboardEntryKind.Image, null, OnePixelPng, []);

        var drag = await ClipboardDragService.CreateAsync(
            payload,
            storage: null,
            ClipboardDragMode.InlineOnly);

        drag.ShouldNotBeNull();
        ((IDataTransfer)drag!.Transfer).Contains(DataFormat.Bitmap).ShouldBeTrue();

        drag.Lifetime?.Dispose();
    }

    [Fact]
    public async Task 文件条目_内联模式不提供任何格式()
    {
        var payload = new ClipboardPastePayload(ClipboardEntryKind.Files, null, null, [@"C:\a.txt"]);

        (await ClipboardDragService.CreateAsync(
            payload,
            storage: null,
            ClipboardDragMode.InlineOnly)).ShouldBeNull();
    }
}
