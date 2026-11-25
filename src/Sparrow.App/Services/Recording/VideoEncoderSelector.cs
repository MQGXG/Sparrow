using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sparrow.App.Services.Recording;

internal enum VideoEncoderKind
{
    Nvenc,
    Amf,
    Qsv,
    MediaFoundation,
    Software
}

internal readonly record struct VideoEncoderChoice(string Name, VideoEncoderKind Kind)
{
    public bool IsHardware => Kind != VideoEncoderKind.Software;
}

/// <summary>
/// 选择 H.264 编码器：按显卡厂商生成候选（NVIDIA → nvenc，AMD → amf，Intel → qsv），
/// 每个候选先用 0.2 秒的测试编码确认能初始化，最后回退 Media Foundation / libx264。
///
/// 只看显卡型号不够：虚拟机、远程桌面、Optimus、老驱动都会让硬编初始化失败，
/// 所以必须真实试编一次。
/// </summary>
internal static class VideoEncoderSelector
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> ProbeResults = new(StringComparer.OrdinalIgnoreCase);

    private static VideoEncoderChoice? _resolved;

    /// <summary>解析本次要用的编码器；结果缓存，候选都失败时回退 libx264。</summary>
    /// <param name="ffmpegPath">ffmpeg.exe 路径。</param>
    /// <param name="preferred">指定编码器（"auto" 或 null 表示自动）。</param>
    public static VideoEncoderChoice Resolve(string ffmpegPath, string? preferred = null)
    {
        lock (Gate)
        {
            var requested = string.IsNullOrWhiteSpace(preferred)
                            || preferred.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? null
                : preferred.Trim();

            // 调试/兜底覆盖：SPARROW_RECORD_ENCODER=h264_qsv / libx264 ...
            requested ??= Environment.GetEnvironmentVariable("SPARROW_RECORD_ENCODER") is { Length: > 0 } env
                ? env.Trim()
                : null;

            if (requested is null && _resolved is { } cached)
                return cached;

            var candidates = Candidates();

            if (requested is not null)
            {
                var forcedChoice = candidates
                        .Where(item => item.Name.Equals(requested, StringComparison.OrdinalIgnoreCase))
                        .Select(item => (VideoEncoderChoice?)item)
                        .FirstOrDefault()
                    ?? new VideoEncoderChoice(requested, KindFromName(requested));

                candidates = new[] { forcedChoice }
                    .Concat(candidates.Where(
                        item => !item.Name.Equals(forcedChoice.Name, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            foreach (var candidate in candidates)
            {
                if (Probe(ffmpegPath, candidate))
                {
                    if (requested is null)
                        _resolved = candidate;

                    return candidate;
                }
            }

            // libx264 一般不会失败；真失败时也让调用方拿到软编再报错。
            _resolved = new VideoEncoderChoice("libx264", VideoEncoderKind.Software);
            return _resolved.Value;
        }
    }

    /// <summary>面板用：列出各编码器在当前机器上是否可用（会真实试编一次）。</summary>
    public static IReadOnlyDictionary<string, bool> GetAvailability(string ffmpegPath)
    {
        lock (Gate)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in Candidates())
                result[candidate.Name] = Probe(ffmpegPath, candidate);

            return result;
        }
    }

    /// <summary>按已安装显卡生成候选顺序。</summary>
    private static List<VideoEncoderChoice> Candidates()
    {
        var adapters = DisplayAdapterNames();

        var hasNvidia = adapters.Any(name =>
            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

        var hasAmd = adapters.Any(name =>
            name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

        var hasIntel = adapters.Any(name =>
            name.Contains("Intel", StringComparison.OrdinalIgnoreCase));

        var list = new List<VideoEncoderChoice>();

        if (hasNvidia)
            list.Add(new VideoEncoderChoice("h264_nvenc", VideoEncoderKind.Nvenc));

        if (hasAmd)
            list.Add(new VideoEncoderChoice("h264_amf", VideoEncoderKind.Amf));

        if (hasIntel)
            list.Add(new VideoEncoderChoice("h264_qsv", VideoEncoderKind.Qsv));

        // 系统 MFT：没有可用硬编时兜底（远程桌面 / 虚拟机也可能走它）。
        list.Add(new VideoEncoderChoice("h264_mf", VideoEncoderKind.MediaFoundation));

        // 最终兜底：永远可用。
        list.Add(new VideoEncoderChoice("libx264", VideoEncoderKind.Software));

        return list;
    }

    private static VideoEncoderKind KindFromName(string name) => name.ToLowerInvariant() switch
    {
        "h264_nvenc" => VideoEncoderKind.Nvenc,
        "h264_amf" => VideoEncoderKind.Amf,
        "h264_qsv" => VideoEncoderKind.Qsv,
        "h264_mf" => VideoEncoderKind.MediaFoundation,
        _ => VideoEncoderKind.Software
    };

    /// <summary>真实试编一次，确认编码器在当前驱动/会话里能初始化。</summary>
    private static bool Probe(string ffmpegPath, VideoEncoderChoice choice)
    {
        if (ProbeResults.TryGetValue(choice.Name, out var known))
            return known;

        var pixelFormat = choice.IsHardware ? "nv12" : "yuv420p";

        var arguments =
            "-hide_banner -loglevel quiet " +
            "-f lavfi -i color=black:s=256x256:d=0.2 -frames:v 1 " +
            $"-c:v {choice.Name} -pix_fmt {pixelFormat} -f null -";

        var ok = false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpegPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            });

            if (process is not null)
            {
                if (process.WaitForExit(8_000))
                {
                    ok = process.ExitCode == 0;
                }
                else
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 进程可能刚好退出。
                    }
                }
            }
        }
        catch
        {
            ok = false;
        }

        ProbeResults[choice.Name] = ok;
        return ok;
    }

    /// <summary>枚举显示器适配器名字（不依赖 WMI）。</summary>
    private static List<string> DisplayAdapterNames()
    {
        var names = new List<string>();

        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new DisplayDevice
                {
                    cb = Marshal.SizeOf<DisplayDevice>()
                };

                if (!EnumDisplayDevices(null, index, ref device, 0))
                    break;

                if (!string.IsNullOrWhiteSpace(device.DeviceString))
                    names.Add(device.DeviceString);
            }
        }
        catch
        {
            // 枚举失败就按“没有可用硬编”处理，最终回退 libx264。
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);
}
