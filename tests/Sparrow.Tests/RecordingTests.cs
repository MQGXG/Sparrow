using System.Buffers.Binary;
using System.Text;
using Avalonia;
using Shouldly;
using Sparrow.App.Services.Recording;
using Xunit;

namespace Sparrow.Tests;

/// <summary>录制链路里不依赖 Win32 / ffmpeg 的纯逻辑。</summary>
public sealed class RecordingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "sparrow-tests", Guid.NewGuid().ToString("N"));

    public RecordingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响结论
        }
    }

    [Fact]
    public void WAV_写入样本后回填RIFF与data长度()
    {
        var path = Path.Combine(_directory, "audio.wav");
        var samples = new short[] { 1, -2, 3, -4 };

        using (var writer = new WavWriter(path))
        {
            writer.Write(samples);
            writer.Complete();
        }

        var bytes = File.ReadAllBytes(path);

        Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4))
            .ShouldBe((uint)(36 + samples.Length * 2));
        Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WAVE");
        Encoding.ASCII.GetString(bytes, 36, 4).ShouldBe("data");
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4))
            .ShouldBe((uint)(samples.Length * 2));

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + index * 2, 2))
                .ShouldBe(samples[index]);
        }
    }

    [Fact]
    public void 码率_显式值优先于估算()
    {
        var options = new RecordingOptions
        {
            Region = new PixelRect(0, 0, 320, 240),
            OutputPath = "out.mp4",
            Bitrate = 8_000_000
        };

        options.ResolveBitrate().ShouldBe(8_000_000);
    }

    [Fact]
    public void 码率_小区域落到下限()
    {
        var options = new RecordingOptions
        {
            Region = new PixelRect(0, 0, 320, 240),
            OutputPath = "out.mp4"
        };

        options.ResolveBitrate().ShouldBe(2_000_000);
    }

    [Fact]
    public void 码率_4K60落到上限()
    {
        var options = new RecordingOptions
        {
            Region = new PixelRect(0, 0, 3840, 2160),
            OutputPath = "out.mp4",
            FramesPerSecond = 60
        };

        options.ResolveBitrate().ShouldBe(40_000_000);
    }

    [Fact]
    public void 码率_1080p30按像素估算()
    {
        var options = new RecordingOptions
        {
            Region = new PixelRect(0, 0, 1920, 1080),
            OutputPath = "out.mp4"
        };

        options.ResolveBitrate().ShouldBe((int)(1920L * 1080 * 30 * 0.12));
    }
}
