using Avalonia.Controls;

namespace Sparrow.App.Shell;

/// <summary>
/// 托盘图标。
/// 用内嵌 PNG 而不是外部文件：单文件发布时不需要额外的资源路径处理，
/// 也让「双击就能跑」这件事少一个文件依赖。
/// </summary>
internal static class TrayIconFactory
{
    // 32x32，雀羽琥珀圆环 + 深色内核
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAAFiUAABYlAUlSJPAAAAI+SURBVFhH1ZY9SCNBFMe3cHbFzygqlnZayRVXCNd4cKBYWQgKItgpgl8YN3qN6a4RLMNVlpYpxUorbQRBxGxsUlpaWsb3Ng+zO/sfsxvHwj/8SJH33n925s2H8y1V23ZylYI3HeUh3zUqf3+NHv2OqWrBLVV9VaPfOkY9P/nuKcUsSdrnVc2rH1XfvcSGZoKCe8czI2XaE33JP1Q8I6XakdMpJdOJ15imswyKtcsN15TyrWXZPISXJNVMWJp2A6osNlhhw8FEewS+uyN2SVHAjZ5gH/UMlyLIe7M4wT6V/Y51sW0qPEBAsIn7vFc/W+kKud31YIwROlfEtimeGhgM+DvTWx8eGq73DYy8s/m7PxwUitehPniNbcvg0J1AgYi9P30x4yjLUzmYg+AlF3vH4SMTBelcb3mJL9e5WOuEuTqVgloVex6AWkVBOv8Xu6FplOJcL8xNoopiTwOgrsRBcY7ne6BpFO4PlJskMoC0W5CnF5lG4V2BcnViWzHLCbjwMweNmV/jgzAHEey782JPtx+dTLw1UKAO7/mZyUFofrWRrgEZavwxsW+ILwoUaIKnmrckw82JYkzwzSi2TaVtRCvQjSu2TfEyZDkN24WX2viA5cckSrKLOhE7LAo4x4kW8FWl5dMsfA9SICzwCajxXhKdb1J4OX34/s8Gm/NZI+XTqfEytvBCotlM/eW6ZGcUwy9AxT+gcbCpk0zPcZO4CA3iIFVvNJau1PZXtxIX5nOcZyYGbWHuHQn7LnKcN4VD/fIcjiRPAAAAAElFTkSuQmCC";

    public static WindowIcon Create()
    {
        var bytes = Convert.FromBase64String(PngBase64);
        return new WindowIcon(new MemoryStream(bytes));
    }
}
