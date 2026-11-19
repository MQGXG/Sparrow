using Microsoft.Data.Sqlite;
using Shouldly;
using Sparrow.App;
using Sparrow.App.Services;
using Sparrow.App.ViewModels;
using Xunit;

namespace Sparrow.Tests;

/// <summary>富文本：HTML / RTF 原样存取，复制时保留、纯文本复制丢弃。</summary>
public sealed class ClipboardRichTextTests
{
    private sealed class NullLog : IAppLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public byte[]? Html { get; private set; }
        public byte[]? Rtf { get; private set; }

        public string? GetText() => Text;
        public byte[]? GetImagePng() => null;
        public bool SetImagePng(byte[] png) => true;

        public bool SetText(string text)
        {
            Text = text;
            Html = null;
            Rtf = null;
            return true;
        }

        public bool SetRichText(string text, byte[]? html, byte[]? rtf)
        {
            Text = text;
            Html = html;
            Rtf = rtf;
            return true;
        }
    }

    private static string NewDirectory()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string directory)
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

    [Fact]
    public void 富文本随条目原样存取()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"), new NullLog());
            var html = "<html><body><b>加粗</b></body></html>"u8.ToArray();
            var rtf = "{\\rtf1\\b bold}"u8.ToArray();

            store.AddText("加粗", html: html, rtf: rtf);
            var entry = store.Recent(1)[0];

            var (savedHtml, savedRtf) = store.LoadRichText(entry.Id);
            savedHtml.ShouldBe(html);
            savedRtf.ShouldBe(rtf);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void 旧库自动补富文本列()
    {
        var directory = NewDirectory();
        try
        {
            var path = System.IO.Path.Combine(directory, "clipboard.db");

            // 模拟 user_version = 1 的旧库：没有 html / rtf 列
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE entries (
                        id           TEXT PRIMARY KEY,
                        kind         INTEGER NOT NULL,
                        preview      TEXT NOT NULL,
                        text         TEXT,
                        image_file   TEXT,
                        paths        TEXT NOT NULL,
                        content_hash TEXT NOT NULL,
                        byte_size    INTEGER NOT NULL,
                        created_at   INTEGER NOT NULL,
                        favorite     INTEGER NOT NULL DEFAULT 0,
                        tags         TEXT NOT NULL DEFAULT '[]',
                        auto_tags    TEXT NOT NULL DEFAULT '[]',
                        source_app   TEXT
                    );
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            var store = new ClipboardHistoryStore(path, new NullLog());
            var html = "<b>迁移</b>"u8.ToArray();
            store.AddText("迁移", html: html);

            var (savedHtml, _) = store.LoadRichText(store.Recent(1)[0].Id);
            savedHtml.ShouldBe(html, "旧库升级后应能写入并读回富文本");
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void 复制保留格式_纯文本复制丢弃()
    {
        var directory = NewDirectory();
        try
        {
            var store = new ClipboardHistoryStore(
                System.IO.Path.Combine(directory, "clipboard.db"), new NullLog());
            var html = "<b>格式</b>"u8.ToArray();
            var rtf = "{\\rtf1 格式}"u8.ToArray();
            store.AddText("格式", html: html, rtf: rtf);

            var clipboard = new FakeClipboard();
            var viewModel = new ClipboardHistoryViewModel(store, clipboard);
            var item = viewModel.Items[0];

            viewModel.Copy(item);
            clipboard.Text.ShouldBe("格式");
            clipboard.Html.ShouldBe(html, "复制应带上原 HTML");
            clipboard.Rtf.ShouldBe(rtf, "复制应带上原 RTF");

            viewModel.CopyPlainText(item);
            clipboard.Text.ShouldBe("格式");
            clipboard.Html.ShouldBeNull("纯文本复制不带 HTML");
            clipboard.Rtf.ShouldBeNull("纯文本复制不带 RTF");
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
