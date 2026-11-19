using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LongshotLauncher.Core;

/// The controllers plugged in, read by the window's thread about 60 times a second (App/PadInput.cs)
public interface IPadSource : IDisposable
{
    /// Each controller's state now, by an id of its own; none plugged in: empty
    IReadOnlyList<(string Id, PadState State)> Read();
}

public static class PadSources
{
    /// The platform's controllers: XInput on Windows (what Steam gives a game too), evdev on Linux; null where neither can be read
    public static IPadSource? Open(Func<string, string?> env)
    {
        if (OperatingSystem.IsWindows()) return new XInputPads();
        if (OperatingSystem.IsLinux()) return new EvdevPads(new PadFilter(env));
        return null;
    }
}

/// Windows: XInput's four slots. A slot with no controller is asked again only every 2 s (asking an empty slot is slow)
sealed class XInputPads : IPadSource
{
    readonly long[] emptyUntil = new long[4];
    bool missing;

    public IReadOnlyList<(string Id, PadState State)> Read()
    {
        var pads = new List<(string, PadState)>();
        if (missing) return pads;
        var now = Environment.TickCount64;
        for (var slot = 0; slot < 4; slot++)
        {
            if (now < emptyUntil[slot]) continue;
            int result;
            XInputState s;
            try { result = XInputGetState(slot, out s); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                missing = true;
                break;
            }
            if (result != 0)
            {
                emptyUntil[slot] = now + 2000;
                continue;
            }
            pads.Add(("xinput" + slot, State(s.Buttons, s.ThumbLX, s.ThumbLY, s.ThumbRY)));
        }
        return pads;
    }

    /// XInput's buttons and sticks (Y up) as a PadState (Y down)
    internal static PadState State(ushort buttons, short lx, short ly, short ry)
    {
        var b = PadButtons.None;
        void Map(int bit, PadButtons to) { if ((buttons & bit) != 0) b |= to; }
        Map(0x0001, PadButtons.Up); Map(0x0002, PadButtons.Down); Map(0x0004, PadButtons.Left); Map(0x0008, PadButtons.Right);
        Map(0x0010, PadButtons.Start); Map(0x0020, PadButtons.Select); Map(0x0100, PadButtons.Lb); Map(0x0200, PadButtons.Rb);
        Map(0x1000, PadButtons.A); Map(0x2000, PadButtons.B); Map(0x4000, PadButtons.X); Map(0x8000, PadButtons.Y);
        return new PadState(b, Axis(lx), -Axis(ly), -Axis(ry));
    }

    static double Axis(short v) => Math.Clamp(v / 32767.0, -1, 1);

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    // (xinput1_4.dll: Windows 8 and later, so every Windows the launcher runs on)
    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(int userIndex, out XInputState state);
}

/// One Linux controller's evdev events made into a PadState: the buttons by their Linux names (BTN_SOUTH is A), the d-pad as
/// buttons or as a hat, the sticks over the range the device gives
public sealed class EvdevPad
{
    public const ushort EvKey = 1, EvAbs = 3;
    public const ushort AbsX = 0, AbsY = 1, AbsRy = 4, AbsHat0X = 0x10, AbsHat0Y = 0x11;
    public const ushort BtnSouth = 0x130;

    PadButtons buttons, hat;
    double x, y, ry;
    readonly Func<ushort, (int Min, int Max)> range;

    /// range: an axis's smallest and largest values (EVIOCGABS)
    public EvdevPad(Func<ushort, (int Min, int Max)> range) => this.range = range;

    public PadState State => new(buttons | hat, x, y, ry);

    public void Apply(ushort type, ushort code, int value)
    {
        if (type == EvKey && Button(code) is { } b)
        {
            if (value != 0) buttons |= b;
            else buttons &= ~b;
        }
        else if (type == EvAbs)
        {
            switch (code)
            {
                case AbsX: x = Normal(code, value); break;
                case AbsY: y = Normal(code, value); break;
                case AbsRy: ry = Normal(code, value); break;
                case AbsHat0X: hat = (hat & ~(PadButtons.Left | PadButtons.Right)) | (value < 0 ? PadButtons.Left : value > 0 ? PadButtons.Right : 0); break;
                case AbsHat0Y: hat = (hat & ~(PadButtons.Up | PadButtons.Down)) | (value < 0 ? PadButtons.Up : value > 0 ? PadButtons.Down : 0); break;
            }
        }
    }

    double Normal(ushort code, int value)
    {
        var (min, max) = range(code);
        if (max <= min) return 0;
        return Math.Clamp((value - (min + max) / 2.0) / ((max - min) / 2.0), -1, 1);
    }

    static PadButtons? Button(ushort code) => code switch
    {
        0x130 => PadButtons.A, 0x131 => PadButtons.B, 0x133 => PadButtons.Y, 0x134 => PadButtons.X, // BTN_SOUTH, EAST, NORTH, WEST
        0x136 => PadButtons.Lb, 0x137 => PadButtons.Rb, 0x13a => PadButtons.Select, 0x13b => PadButtons.Start,
        0x220 => PadButtons.Up, 0x221 => PadButtons.Down, 0x222 => PadButtons.Left, 0x223 => PadButtons.Right, // BTN_DPAD_*
        _ => null,
    };

    /// Whether a device's sysfs key capability bitmap (capabilities/key: hex words, the highest first) has BTN_SOUTH: what
    /// makes it a gamepad (SDL's test too)
    public static bool IsGamepad(string keyBitmap)
    {
        var words = keyBitmap.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = words.Length - 1 - BtnSouth / 64;
        return index >= 0 && ulong.TryParse(words[index], System.Globalization.NumberStyles.HexNumber, null, out var word)
            && (word >> (BtnSouth % 64) & 1) == 1;
    }
}

/// Linux: each /dev/input/event* that is a gamepad and that Steam does not hide (PadFilter), read on a thread of its own; new
/// ones looked for every 2 s. A device the user cannot read (no uaccess) is skipped
[SupportedOSPlatform("linux")]
sealed class EvdevPads : IPadSource
{
    // (struct input_event on 64-bit Linux: a 16-byte timeval, then type, code, value)
    const int EventSize = 24;

    readonly PadFilter filter;
    readonly Dictionary<string, Device> devices = new();
    readonly HashSet<string> skipped = new();
    long nextScan;
    bool disposed;

    public EvdevPads(PadFilter filter) => this.filter = filter;

    sealed class Device
    {
        public required FileStream Stream;
        public required EvdevPad Pad;
        public volatile bool Gone;
    }

    public IReadOnlyList<(string Id, PadState State)> Read()
    {
        if (Environment.TickCount64 >= nextScan)
        {
            nextScan = Environment.TickCount64 + 2000;
            Scan();
        }
        var pads = new List<(string, PadState)>();
        foreach (var (path, device) in devices.ToList())
        {
            if (device.Gone)
            {
                devices.Remove(path);
                device.Stream.Dispose();
                continue;
            }
            lock (device.Pad) pads.Add((path, device.Pad.State));
        }
        return pads;
    }

    void Scan()
    {
        string[] nodes;
        try { nodes = Directory.GetFiles("/dev/input", "event*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        skipped.IntersectWith(nodes);
        foreach (var node in nodes)
        {
            if (devices.ContainsKey(node) || skipped.Contains(node)) continue;
            var sys = Path.Combine("/sys/class/input", Path.GetFileName(node), "device");
            try
            {
                if (!EvdevPad.IsGamepad(File.ReadAllText(Path.Combine(sys, "capabilities", "key")))
                    || !filter.Allows(Hex(Path.Combine(sys, "id", "vendor")), Hex(Path.Combine(sys, "id", "product"))))
                {
                    skipped.Add(node);
                    continue;
                }
                var stream = new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
                var fd = (int)stream.SafeFileHandle.DangerousGetHandle();
                var ranges = new Dictionary<ushort, (int, int)>();
                foreach (var axis in new[] { EvdevPad.AbsX, EvdevPad.AbsY, EvdevPad.AbsRy })
                    ranges[axis] = AbsRange(fd, axis);
                var device = new Device { Stream = stream, Pad = new EvdevPad(a => ranges.TryGetValue(a, out var r) ? r : (-1, 1)) };
                devices[node] = device;
                new Thread(() => Pump(device)) { IsBackground = true, Name = "controller " + Path.GetFileName(node) }.Start();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
            {
                skipped.Add(node);
            }
        }
    }

    static void Pump(Device device)
    {
        var buffer = new byte[EventSize * 32];
        try
        {
            while (true)
            {
                var n = device.Stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                lock (device.Pad)
                    for (var at = 0; at + EventSize <= n; at += EventSize)
                        device.Pad.Apply(BitConverter.ToUInt16(buffer, at + 16), BitConverter.ToUInt16(buffer, at + 18), BitConverter.ToInt32(buffer, at + 20));
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) { }
        device.Gone = true;
    }

    static int Hex(string file) => int.Parse(File.ReadAllText(file).Trim(), System.Globalization.NumberStyles.HexNumber);

    /// EVIOCGABS: an axis's range; a device that does not say: XInput's
    static (int, int) AbsRange(int fd, ushort axis)
    {
        var request = (2u << 30) | ((uint)Marshal.SizeOf<AbsInfo>() << 16) | ((uint)'E' << 8) | (0x40u + axis);
        try
        {
            if (ioctl(fd, request, out var info) == 0 && info.Maximum > info.Minimum) return (info.Minimum, info.Maximum);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return (-32768, 32767);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AbsInfo
    {
        public int Value, Minimum, Maximum, Fuzz, Flat, Resolution;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(int fd, nuint request, out AbsInfo info);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var device in devices.Values) device.Stream.Dispose();
        devices.Clear();
    }
}
