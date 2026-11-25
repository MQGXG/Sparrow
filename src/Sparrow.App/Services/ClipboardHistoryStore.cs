using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sparrow.Plugin.Sdk;

namespace Sparrow.App.Services;

/// <summary>
/// 剪贴板历史的 SQLite 实现（WAL + FTS5 trigram）。
///
/// 为什么不用 JSONL：每条记录变更都要整文件重写，搜索只能内存全扫；
/// 数据库给的是行级更新、事务写入和索引搜索。图片仍然放文件，
/// 数据库只存文件名——预览、拖出都直接读文件，备份也直观。
///
/// 线程模型：监听线程写、UI 线程读；所有操作在 <see cref="_gate"/> 内、
/// 每次操作独立连接（连接池），避免跨线程共享连接。
/// </summary>
public sealed partial class ClipboardHistoryStore : IClipboardHistory
{
    private const int DefaultMaxEntries = 200;

    /// <summary>超过 32MB 的图片不记录：占盘且几乎不会还原使用。</summary>
    private const long MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>超过 256K 字符的文本只记摘要，不记全文（与 TextInput 截断阈值一致）。</summary>
    private const int MaxTextChars = 256 * 1024;

    private const string SelectColumns =
        "id, kind, preview, text, image_file, paths, content_hash, byte_size, created_at, favorite, tags, auto_tags, source_app";

    /// <summary>与 FTS 表 JOIN 时必须带表别名，否则 preview / text 等列名会歧义。</summary>
    private const string SelectColumnsJoined =
        "e.id, e.kind, e.preview, e.text, e.image_file, e.paths, e.content_hash, e.byte_size, e.created_at, e.favorite, e.tags, e.auto_tags, e.source_app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // 中文不要转成 \uXXXX：标签/路径要能直接 LIKE 和肉眼看
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly string _imagesDirectory;
    private readonly string _legacyJsonlPath;
    private readonly string _connectionString;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    private readonly bool _ftsEnabled;
    private int _maxEntries;
    private int _retentionDays;
    private long _maxBytes;
    private long _lastTimestampMs;

    public ClipboardHistoryStore(
        string path,
        IAppLog log,
        int maxEntries = DefaultMaxEntries,
        int retentionDays = 0,
        long maxBytes = 0)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);

        _imagesDirectory = Path.Combine(directory, "clipboard-images");
        _legacyJsonlPath = Path.Combine(directory, "clipboard-history.jsonl");
        _log = log;
        _maxEntries = Math.Max(1, maxEntries);
        _retentionDays = Math.Max(0, retentionDays);
        _maxBytes = Math.Max(0, maxBytes);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var isNewDatabase = !File.Exists(path);

        _ftsEnabled = InitializeDatabase();
        _lastTimestampMs = ReadMaxTimestamp();

        // 只有新建数据库才迁移旧 JSONL，避免「清空历史后重启又被旧数据灌回来」
        if (isNewDatabase)
            MigrateLegacyJsonl();

        _lastTimestampMs = Math.Max(_lastTimestampMs, ReadMaxTimestamp());
        TrimStandalone();
    }

    /// <summary>当前构建是否启用了 FTS5 trigram 索引；不可用时搜索自动退化 LIKE。</summary>
    public bool UsesFullTextIndex => _ftsEnabled;

    public int Count => (int)Scalar("SELECT COUNT(*) FROM entries");

    /// <summary>普通记录（不含收藏）的条数上限，用于状态栏显示配额。</summary>
    public int MaxEntries => _maxEntries;

    /// <summary>总容量上限（字节）；0 表示不限制。</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>历史占用的近似字节数：文本按字符数、图片按 PNG 字节数。</summary>
    public long ApproximateBytes => Scalar("SELECT COALESCE(SUM(byte_size), 0) FROM entries");

    /// <summary>历史发生变化（新增 / 收藏 / 标签 / 删除）。监听来自后台线程，订阅方自行切到 UI 线程。</summary>
    public event Action? Changed;


    /// <summary>保留条数变化时调用；立即裁剪一次。</summary>
    public void Configure(int maxEntries)
    {
        lock (_gate)
        {
            _maxEntries = Math.Max(1, maxEntries);
            TrimStandalone();
        }

        Changed?.Invoke();
    }

    /// <summary>保留策略（天数 / 容量）变化时调用；立即按新策略清理一次。</summary>
    public void ConfigureRetention(int retentionDays, long maxBytes)
    {
        lock (_gate)
        {
            _retentionDays = Math.Max(0, retentionDays);
            _maxBytes = Math.Max(0, maxBytes);
            TrimStandalone();
        }

        Changed?.Invoke();
    }
}
