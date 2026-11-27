namespace Sparrow.App.Services;

/// <summary>剪贴板列表的时间分叠规则；抽成纯函数便于测试。</summary>
public static class ClipboardTimeGroup
{
    public const string Favorites = "收藏";
    public const string Today = "今天";
    public const string ThreeDays = "三日内";
    public const string Week = "一周内";
    public const string Older = "更早";

    public static string Resolve(DateTimeOffset createdAt, DateTimeOffset now)
    {
        // 【修改】以调用方传入的 now 偏移判定“今天”，而不是进程本地时区。
        // CI 通常运行在 UTC，若用 LocalDateTime，+08 时区凌晨的条目会被算成昨天。
        if (createdAt.ToOffset(now.Offset).Date == now.Date)
            return Today;

        var age = now - createdAt;

        return age.TotalDays switch
        {
            < 3 => ThreeDays,
            < 7 => Week,
            _ => Older
        };
    }
}
