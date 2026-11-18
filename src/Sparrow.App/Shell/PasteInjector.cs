using System.Runtime.InteropServices;

namespace Sparrow.App.Shell;

/// <summary>
/// 把「粘贴」注入到当前前台窗口。
///
/// 顺序粘贴的热键本身带着 Alt（默认 Ctrl+Alt+V），如果直接再发 Ctrl+V，
/// 目标应用会看到 Ctrl+Alt+V——又会命中我们自己的热键。
/// 所以先把 Alt / Shift / Win 松开，只保留 Ctrl 再按 V。
/// </summary>
public static class PasteInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkV = 0x56;

    // RegisterHotKey 的修饰符位：Alt=1、Control=2、Shift=4、Win=8
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;

    public static bool SendPaste(uint hotkeyModifiers)
    {
        var inputs = new List<INPUT>(6);

        if ((hotkeyModifiers & ModAlt) != 0)
            AddKey(inputs, VkAlt, keyUp: true);

        if ((hotkeyModifiers & ModShift) != 0)
            AddKey(inputs, VkShift, keyUp: true);

        if ((hotkeyModifiers & ModWin) != 0)
            AddKey(inputs, VkLeftWin, keyUp: true);

        var controlHeld = (hotkeyModifiers & ModControl) != 0;

        if (!controlHeld)
            AddKey(inputs, VkControl, keyUp: false);

        AddKey(inputs, VkV, keyUp: false);
        AddKey(inputs, VkV, keyUp: true);

        if (!controlHeld)
            AddKey(inputs, VkControl, keyUp: true);

        var array = inputs.ToArray();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
    }

    private static void AddKey(List<INPUT> inputs, ushort virtualKey, bool keyUp) =>
        inputs.Add(new INPUT
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        });

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;

        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);
}
