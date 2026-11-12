using System.Text;

// 假 worker：不接真实 OCR 模型，只验证「宿主 → 子进程 → 宿主」这条管道。
//
// 协议（M1，最小）：
//   stdin  = [4 字节小端长度][PNG 图片字节]
//   stdout = UTF-8 识别文本
//   exit 0 = 成功；非 0 = 失败，stderr 带原因
//
// 将来换成 Paddle 时，协议不用变：把「假识别」换成模型推理即可。

using var stdin = Console.OpenStandardInput();

var lengthBytes = new byte[4];
ReadExactly(stdin, lengthBytes);
var length = BitConverter.ToInt32(lengthBytes);

if (length <= 0)
{
    Console.Error.Write("图片长度为 0");
    return 2;
}

var image = new byte[length];
ReadExactly(stdin, image);

// 假识别：报告收到的字节数，证明图片确实跨进程传过来了
var result = $"OCR 假 worker：收到 {image.Length} 字节图片（M1 仅验证 IPC）";
var resultBytes = Encoding.UTF8.GetBytes(result);

using var stdout = Console.OpenStandardOutput();
stdout.Write(resultBytes, 0, resultBytes.Length);
return 0;

static void ReadExactly(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);
        if (read == 0)
            throw new EndOfStreamException("stdin 提前结束");

        offset += read;
    }
}
