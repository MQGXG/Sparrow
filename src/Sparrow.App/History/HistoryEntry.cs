namespace Sparrow.App.History;

/// <summary>一条工具执行记录。</summary>
public sealed record HistoryEntry(
    string Id,
    string ToolId,
    string ToolName,
    string Output,
    string InputPreview,
    DateTimeOffset CreatedAt,
    long DurationMs);
