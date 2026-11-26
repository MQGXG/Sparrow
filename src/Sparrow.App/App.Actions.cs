// App 分部类：截图动作分发、OCR、复制与历史记录。
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Sparrow.App.History;
using Sparrow.App.Overlays;
using Sparrow.App.Plugins;
using Sparrow.App.Services;
using Sparrow.App.Settings;
using Sparrow.App.Shell;
using Sparrow.App.ViewModels;
using Sparrow.App.Views;
using Sparrow.Plugin.Sdk;
using Sparrow.Plugin.Sdk.Inputs;
using Sparrow.Plugin.Sdk.Payloads;

namespace Sparrow.App;

public partial class App
{
    /// <summary>截图结果的动作分发：普通截图和滚动截图共用。</summary>
    private async Task HandleCaptureActionAsync(byte[] png, CaptureAction action)
    {
        switch (action)
        {
            case CaptureAction.CopyImage:
                CopyImageToClipboard(png);
                return;

            case CaptureAction.Save:
                await SaveImageAsync(png);
                return;

            case CaptureAction.Pin:
                PinImage(png);
                return;

            case CaptureAction.Recognize:
                await RecognizeImageAsync(png);
                return;
        }
    }

    private void CopyImageToClipboard(byte[] png)
    {
        try
        {
            var clipboardForImage = new Win32Clipboard(Log);
            if (clipboardForImage.SetImagePng(png))
                Log.Info("截图已复制到剪贴板");
            else
                Log.Warn("复制图片到剪贴板失败");
        }
        catch (Exception ex)
        {
            Log.Error("复制图片到剪贴板失败", ex);
        }
    }

    /// <summary>
    /// 截图窗口里的就地 OCR：宿主准备输入，插件仍然只收一张 ImageInput。
    /// 识别成功写历史；旧插件只返回文本时由窗口回退到面板。
    /// </summary>
    private async Task<CaptureOcrResult> RunCaptureOcrAsync(
        byte[] png,
        CaptureOcrOptions options,
        IProgress<IReadOnlyList<OcrTextLine>>? partialLines,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
            return CaptureOcrResult.Failed("工具目录尚未就绪");

        var entry = _catalog.GetRegistry()
            .FindAccepting(InputShape.Images)
            .FirstOrDefault();

        if (entry is null)
            return CaptureOcrResult.Failed("没有能识别图片的工具");

        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var parameters = BuildParameters(entry);

            // 截图工具的「翻译」下拉：覆盖 OCR 上次的选择，本次生效
            if (options.TranslateTarget is { Length: > 0 } target)
                parameters[CaptureOcrParameters.Translate] = target;

            // 引擎：auto / ai / local:{模型Id}；空表示沿用参数里保存的值
            if (options.Engine is { Length: > 0 } engine)
                parameters[CaptureOcrParameters.Engine] = engine;

            // 输出模式：text / table-html / table-md
            if (options.Output is { Length: > 0 } output)
                parameters[CaptureOcrParameters.Output] = output;

            var context = new ToolContext
            {
                Input = new ImageSetInput(
                [
                    new ImageInput(png, ImageFormat.Png) { Name = "截图" }
                ]),
                Source = ToolSource.Screenshot,
                Parameters = parameters,
                Host = entry.Host,
                // 插件每翻译出一部分就上报一次，窗口边收边画
                Progress = partialLines is null
                    ? null
                    : new Progress<ToolProgress>(progress =>
                    {
                        if (progress.Lines is { Count: > 0 } lines)
                            partialLines.Report(lines);
                    })
            };

            var result = await ToolRunner.ExecuteAsync(entry.Tool, context, cancellationToken);

            if (!result.Success)
                return CaptureOcrResult.Failed(result.Error?.Message ?? "识别失败");

            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            switch (result.Payload)
            {
                case OcrResultPayload ocr:
                    AddCaptureHistory(entry, png, ocr.Text, elapsed);
                    return new CaptureOcrResult(ocr.Text, ocr.Lines);

                case TextPayload text:
                    AddCaptureHistory(entry, png, text.Text, elapsed);
                    return new CaptureOcrResult(text.Text, Array.Empty<OcrTextLine>());

                default:
                    return CaptureOcrResult.Failed("识别结果格式不受支持");
            }
        }
        catch (OperationCanceledException)
        {
            return CaptureOcrResult.Failed("已取消");
        }
        catch (Exception ex)
        {
            Log.Error("截图就地识别失败", ex);
            return CaptureOcrResult.Failed($"识别失败：{ex.Message}");
        }
    }

    /// <summary>窗口只负责发命令，剪贴板仍然是宿主能力。</summary>
    private void CopyTextToClipboard(string text)
    {
        try
        {
            var clipboard = new Win32Clipboard(Log);
            if (clipboard.SetText(text))
                Log.Info($"复制文本到剪贴板：{text.Length} 字");
            else
                Log.Warn($"复制文本到剪贴板失败：{text.Length} 字");
        }
        catch (Exception ex)
        {
            Log.Error("复制文本失败", ex);
        }
    }

    /// <summary>工具默认参数 + 上次保存的参数；面板与截图共用。</summary>
    private Dictionary<string, string> BuildParameters(ToolEntry entry)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in entry.Descriptor.Parameters)
        {
            if (parameter.Default is not null)
                parameters[parameter.Key] = parameter.Default;
        }

        if (_parameterStore is not null)
        {
            foreach (var (key, value) in _parameterStore.Get(entry.Descriptor.Id))
                parameters[key] = value;
        }

        return parameters;
    }

    private void AddCaptureHistory(ToolEntry entry, byte[] png, string text, long elapsedMs)
    {
        _history?.Add(new HistoryEntry(
            Guid.NewGuid().ToString("N"),
            entry.Descriptor.Id,
            entry.Descriptor.Name,
            text,
            $"截图 {png.Length} 字节",
            DateTimeOffset.Now,
            elapsedMs));
    }
}
