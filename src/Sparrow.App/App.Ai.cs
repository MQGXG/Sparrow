// App 分部类：AI 发送确认、模型描述与翻译目标/引擎。
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
    /// <summary>
    /// 发送前确认：本次运行第一次调用 AI 时弹一次，展示服务、模型和将要发送的内容。
    /// 取消后请求不会发出；下次调用会重新询问。并发调用共用同一个弹窗。
    /// </summary>
    private Task<bool> ConfirmAiSendAsync(AiRequest request)
    {
        if (_aiConsentGranted)
            return Task.FromResult(true);

        return _aiConsentPending ??= AskAiConsentAsync(request);
    }

    /// <summary>
    /// 截图工具条「翻译」的语言列表：直接读 OCR 工具的「翻译为」声明，跳过「不翻译」。
    /// 读不到（插件还没加载/加载失败）就返回空，按钮自己隐藏。
    /// </summary>
    private IReadOnlyList<(string Value, string Label)> TranslateTargets()
    {
        if (_catalog is null)
            return [];

        try
        {
            var entry = _catalog.GetRegistry()
                .FindAccepting(InputShape.Images)
                .FirstOrDefault();

            var parameter = entry?.Descriptor.Parameters
                .FirstOrDefault(item => item.Key == "translate");

            if (parameter is null)
                return [];

            return parameter.Choices
                .Where(choice => choice != "none")
                .Select(choice => (
                    choice,
                    parameter.ChoiceLabels is { } labels && labels.TryGetValue(choice, out var label)
                        ? label
                        : choice))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取截图翻译语言失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 截图工具条的翻译引擎：在线模型（如果配好）+ 已下载的本地模型。
    /// 只有一个「自动」时返回空，按钮自己隐藏——没有可选项就别放一个空菜单。
    /// </summary>
    private IReadOnlyList<(string Value, string Label)> TranslateEngines()
    {
        var engines = new List<(string Value, string Label)>
        {
            ("auto", "自动（在线优先）")
        };

        try
        {
            if (_ai is { IsAvailable: true })
            {
                var name = string.IsNullOrWhiteSpace(_ai.ModelName)
                    ? "在线模型"
                    : $"在线 · {_ai.ModelName}";
                engines.Add(("ai", name));
            }

            if (_localModels is { } models)
            {
                foreach (var model in models.All)
                {
                    if (models.IsInstalled(model.Id))
                        engines.Add(($"local:{model.Id}", model.Name));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取翻译引擎失败：{ex.Message}");
        }

        return engines.Count > 1 ? engines : [];
    }

    private Task<bool> AskAiConsentAsync(AiRequest request)
    {
        var completion = new TaskCompletionSource<bool>();

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = new AiConsentWindow(
                    DescribeAiTarget(),
                    _appSettings.AiModel,
                    FormatAiPreview(request));

                window.Show();
                var approved = await window.Result;

                if (approved)
                {
                    if (window.Remember)
                        _aiConsentGranted = true;

                    completion.TrySetResult(true);
                }
                else
                {
                    // 用户拒绝：清掉缓存，下次调用重新问
                    _aiConsentPending = null;
                    completion.TrySetResult(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"发送确认弹窗失败，已取消本次请求：{ex.Message}");
                _aiConsentPending = null;
                completion.TrySetResult(false);
            }
        });

        return completion.Task;
    }

    /// <summary>弹窗第一行：提供方名 + 主机名（不显示完整路径和密钥）。</summary>
    private string DescribeAiTarget()
    {
        var provider = AiProviders.Find(_appSettings.AiProvider)?.Name ?? "自定义";
        var host = Uri.TryCreate(_appSettings.AiBaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : _appSettings.AiBaseUrl;

        return $"{provider} · {host}";
    }

    /// <summary>预览要发送的内容：系统提示 + 每条消息，长文本截断。</summary>
    private static string FormatAiPreview(AiRequest request)
    {
        var builder = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.System))
            builder.AppendLine("【系统提示】").AppendLine(request.System.Trim()).AppendLine();

        foreach (var message in request.Messages)
        {
            builder.Append('【').Append(message.Role switch
            {
                AiRole.System => "系统",
                AiRole.Assistant => "模型",
                _ => "用户"
            }).Append("】 ").AppendLine(message.Text);
        }

        if (request.Image.Length > 0)
            builder.AppendLine().Append("【附带图片】").Append(request.ImageMimeType ?? "image");

        var text = builder.ToString().Trim();
        return text.Length <= 800 ? text : text[..800] + $"\n…（共 {text.Length} 字）";
    }
}
