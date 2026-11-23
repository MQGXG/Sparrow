using System.Runtime.InteropServices;

namespace Sparrow.App.Services;

/// <summary>
/// 检测当前是否有启用了 HDR（高级颜色）的显示器。
///
/// snow-shot 的「自动」取帧策略是：HDR 用 WGC，其它情况用 GDI——
/// GDI 在 SDR 下更快，WGC 才能正确拿 HDR 合成结果。
/// </summary>
internal static class HdrDisplay
{
    private const uint QdcOnlyActivePaths = 2;
    private const int GetAdvancedColorInfo = 11;
    private const int AdvancedColorEnabledBit = 1 << 1;

    private static readonly object Gate = new();
    private static bool _cached;
    private static long _cachedAt;

    /// <summary>任一活动显示器启用了 HDR。检测失败按 false 处理（走 GDI）。</summary>
    public static bool AnyEnabled
    {
        get
        {
            lock (Gate)
            {
                var now = Environment.TickCount64;

                // HDR 开关可能中途变化；缓存 5 秒，避免每帧查一遍
                if (now - _cachedAt < 5000)
                    return _cached;

                _cached = Detect();
                _cachedAt = now;
                return _cached;
            }
        }
    }

    private static bool Detect()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(
                    QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return false;
            }

            var pathSize = Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>();
            var modeSize = Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>();

            var paths = Marshal.AllocHGlobal((int)(pathCount * pathSize));
            var modes = Marshal.AllocHGlobal((int)(modeCount * modeSize));

            try
            {
                if (QueryDisplayConfig(
                        QdcOnlyActivePaths,
                        ref pathCount,
                        paths,
                        ref modeCount,
                        modes,
                        IntPtr.Zero) != 0)
                {
                    return false;
                }

                for (var index = 0; index < pathCount; index++)
                {
                    var path = paths + index * pathSize;

                    // targetInfo 紧跟 sourceInfo（20 字节）：targetAdapterId 在 +20，target id 在 +28
                    var adapterId = Marshal.PtrToStructure<LUID>(path + 20);
                    var targetId = Marshal.ReadInt32(path + 28);

                    var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = GetAdvancedColorInfo,
                            size = Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                            adapterId = adapterId,
                            id = targetId
                        }
                    };

                    if (DisplayConfigGetDeviceInfo(ref info) == 0
                        && (info.value & AdvancedColorEnabledBit) != 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(paths);
                Marshal.FreeHGlobal(modes);
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public int size;
        public LUID adapterId;
        public int id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public int colorEncoding;
        public uint bitsPerColorChannel;
    }

    /// <summary>QueryDisplayConfig 的路径数组元素；这里只按大小分配，字段不读取。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public long SourceAdapter;
        public int SourceId;
        public int SourceModeIndex;
        public int SourceStatus;

        public long TargetAdapter;
        public int TargetId;
        public int TargetModeIndex;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public long RefreshRate;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public int TargetStatus;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public int InfoType;
        public int Id;
        public LUID AdapterId;
        public long Padding;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        IntPtr pathArray,
        ref uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(
        ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);
}
