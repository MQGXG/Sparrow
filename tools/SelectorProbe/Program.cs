using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Sparrow.App.Services;

// 诊断工具必须和 Sparrow 一样是 per-monitor DPI 感知：
// 否则 UIA 返回的物理像素坐标会被系统按缩放虚拟化，和真实光标位置错位。
NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: SelectorProbe <pid> [x y ...]");
    return;
}

var process = Process.GetProcessById(int.Parse(args[0]));
var hwnd = process.MainWindowHandle;

if (Environment.GetEnvironmentVariable("SPARROW_SELECTOR_HWND") is { Length: > 0 } rawHwnd)
    hwnd = (IntPtr)Convert.ToInt64(rawHwnd, 16);

if (hwnd == IntPtr.Zero)
{
    Console.Error.WriteLine("no main window");
    return;
}

NativeMethods.GetWindowRect(hwnd, out var rect);
var noForeground = Environment.GetEnvironmentVariable("SPARROW_SELECTOR_NO_FOREGROUND") == "1";
if (!noForeground)
{
    NativeMethods.ShowWindow(hwnd, 9);
    NativeMethods.SetForegroundWindow(hwnd);
    Thread.Sleep(500);
}
Console.WriteLine($"hwnd=0x{hwnd.ToInt64():X} pid={process.Id} title={process.MainWindowTitle}");Console.WriteLine($"rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");

var points = new List<PixelPoint>();
if (args.Length >= 3)
{
    for (var i = 1; i + 1 < args.Length; i += 2)
        points.Add(new PixelPoint(int.Parse(args[i]), int.Parse(args[i + 1])));
}
else
{
    var centerX = (rect.Left + rect.Right) / 2;
    var centerY = (rect.Top + rect.Bottom) / 2;
    points.Add(new PixelPoint(centerX, centerY));
    points.Add(new PixelPoint(rect.Left + 400, rect.Top + 300));
    points.Add(new PixelPoint(rect.Left + 700, rect.Top + 600));
}

using var uia = new UiElementProbe();
using var msaa = new MsaaElementProbe();
var prewarmed = await uia.PrewarmAsync(hwnd, TimeSpan.FromMilliseconds(1500));
Console.WriteLine($"prewarm={prewarmed}");

var candidates = WindowCandidates.Enumerate(IntPtr.Zero);

foreach (var point in points)
{
    Console.WriteLine($"--- point=({point.X},{point.Y})");

    var under = candidates.Where(c => c.Contains(point)).Take(6).ToList();
    foreach (var candidate in under)
    {
        Console.WriteLine(
            $"  under: 0x{candidate.Handle.ToInt64():X} z={candidate.ZOrder} " +
            $"class={candidate.ClassName} {candidate.Title} | {candidate.Bounds.X},{candidate.Bounds.Y} " +
            $"{candidate.Bounds.Width}x{candidate.Bounds.Height}");
    }

    var picked = WindowCandidates.Pick(candidates, point);
    if (picked is not null)
        Console.WriteLine($"  picked: 0x{picked.Handle.ToInt64():X} {picked.Title}");

    var probeHwnd = Environment.GetEnvironmentVariable("SPARROW_SELECTOR_FORCE_HWND") == "1"
        ? hwnd
        : picked?.Handle ?? hwnd;
    var probeTitle = probeHwnd == hwnd ? process.MainWindowTitle : picked?.Title ?? process.MainWindowTitle;

    var uiaResult = await uia.ProbeAsync(
        probeHwnd, point, TimeSpan.FromMilliseconds(170), null, null, probeTitle);
    Print("UIA-170", uiaResult);

    var refineProbe = new UiElementProbe();
    var uiaRefined = await refineProbe.ProbeAsync(
        probeHwnd, point, TimeSpan.FromMilliseconds(1500), null, null, probeTitle,
        refreshTree: true);
    Print("UIA-1500", uiaRefined);

    if (picked is not null)
    {
        var model = new ElementSelectionModel();
        model.ApplyPath(uiaRefined.Path, picked.Bounds, minimumSize: 4);
        Console.WriteLine($"MODEL current={model.Current?.ControlType} " +
                          $"{model.Current?.Bounds} index={model.Index} layers={model.Path.Count}");
    }

    var msaaResult = await msaa.ProbeAsync(
        probeHwnd, point, TimeSpan.FromMilliseconds(300), null, probeTitle);
    Print("MSAA", msaaResult);
}

static void Print(string label, UiElementProbeResult result)
{
    Console.WriteLine($"{label} reason={result.Reason} count={result.Path.Count}");
    foreach (var candidate in result.Path)
    {
        Console.WriteLine(
            $"  {candidate.ControlType} | {candidate.Title} | " +
            $"{candidate.Bounds.X},{candidate.Bounds.Y} {candidate.Bounds.Width}x{candidate.Bounds.Height}");
    }
}

static class NativeMethods
{
    // per-monitor v2（-4）：和 Sparrow.App 保持一致，避免 UIA 物理像素坐标被 DPI 虚拟化
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
