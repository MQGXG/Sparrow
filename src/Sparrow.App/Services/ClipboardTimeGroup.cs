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
        if (createdAt.LocalDateTime.Date == now.LocalDateTime.Date)
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
