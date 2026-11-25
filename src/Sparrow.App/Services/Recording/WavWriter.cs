using System.Runtime.InteropServices;

namespace Sparrow.App.Services.Recording;

/// <summary>
/// 48kHz / 16-bit / 立体声 WAV 写入器。
/// 录制中只追加 PCM，停止时回填 RIFF 与 data 的长度。
/// </summary>
internal sealed class WavWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private long _dataBytes;
    private bool _completed;

    public WavWriter(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream);
        WriteHeader(0);
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return;

        var bytes = MemoryMarshal.AsBytes(samples);
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    /// <summary>回填长度并关闭文件流。必须在 <see cref="Dispose"/> 前调用。</summary>
    public void Complete()
    {
        if (_completed)
            return;

        _completed = true;

        _stream.Seek(4, SeekOrigin.Begin);
        _writer.Write((uint)(36 + _dataBytes));

        _stream.Seek(40, SeekOrigin.Begin);
        _writer.Write((uint)_dataBytes);

        // 关闭文件句柄：否则 FinalizeOutput 删除临时 wav 时会被占用。
        _writer.Dispose();
    }

    private void WriteHeader(uint dataBytes)
    {
        _writer.Write("RIFF"u8);
        _writer.Write((uint)(36 + dataBytes));
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16u);        // fmt chunk 大小
        _writer.Write((ushort)1);  // PCM
        _writer.Write((ushort)2);  // 立体声
        _writer.Write(48000u);     // 采样率
        _writer.Write(192000u);    // 字节率
        _writer.Write((ushort)4);  // block align
        _writer.Write((ushort)16); // 位深
        _writer.Write("data"u8);
        _writer.Write(dataBytes);
    }

    public void Dispose()
    {
        if (_completed)
            return;

        _completed = true;
        _writer.Dispose();
    }
}
