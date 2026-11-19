using Sparrow.App.Shell;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 顺序粘贴要写进剪贴板的内容；只含宿主能处理的三类。
/// </summary>
public sealed record ClipboardPastePayload(
    ClipboardEntryKind Kind,
    string? Text,
    byte[]? Png,
    IReadOnlyList<string> Paths,
    byte[]? Html = null,
    byte[]? Rtf = null);

/// <summary>
/// 顺序粘贴的宿主侧编排：抑制历史记录 → 写剪贴板 →（可选）模拟 Ctrl+V。
///
/// 放在宿主而不是页面 VM：它同时需要剪贴板、监听器和 Win32 输入注入，
/// 这些都不该让页面直接碰。
/// </summary>
public sealed class ClipboardPasteService
{
    private readonly IClipboardAccess _clipboard;
    private readonly ClipboardMonitor? _monitor;
    private readonly IAppLog? _log;

    public ClipboardPasteService(
        IClipboardAccess clipboard,
        ClipboardMonitor? monitor = null,
        IAppLog? log = null)
    {
        _clipboard = clipboard;
        _monitor = monitor;
        _log = log;
    }

    /// <summary>触发顺序粘贴的热键修饰符，决定注入前要松开哪些键。</summary>
    public uint TriggerModifiers { get; set; } = 0x1 | 0x2; // Alt + Control

    public bool Paste(ClipboardPastePayload payload, bool simulateKeystroke)
    {
        // 先抑制再写：监听线程可能比这里返回得更早
        _monitor?.SuppressNextWrite(TimeSpan.FromMilliseconds(800));

        var written = payload.Kind switch
        {
            ClipboardEntryKind.Text when payload.Text is { Length: > 0 } =>
                payload.Html is not null || payload.Rtf is not null
                    ? _clipboard.SetRichText(payload.Text, payload.Html, payload.Rtf)
                    : _clipboard.SetText(payload.Text),

            ClipboardEntryKind.Image when payload.Png is { Length: > 0 } =>
                _clipboard.SetImagePng(payload.Png),

            // 文件条目没有 CF_HDROP 写入口：退化成路径文本，至少能粘到编辑器
            ClipboardEntryKind.Files when payload.Paths.Count > 0 =>
                _clipboard.SetText(string.Join(Environment.NewLine, payload.Paths)),

            _ => false
        };

        if (!written)
        {
            _log?.Warn("顺序粘贴：写入剪贴板失败");
            return false;
        }

        if (simulateKeystroke && !PasteInjector.SendPaste(TriggerModifiers))
        {
            _log?.Warn("顺序粘贴：模拟 Ctrl+V 失败");
            return false;
        }

        return true;
    }
}
