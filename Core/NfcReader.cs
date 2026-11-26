using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LongshotLauncher.Core;

/// What the controller is asked through: one command out, its answer back (NfcReaderUsb.Exchange; a script in the tests). It
/// throws an IOException when the controller does not answer
public delegate byte[] NfcExchange(byte[] command);

/// Read: the tag's 540 bytes are in Tag; NoTag: none was held on the controller in time; Failed: Problem says why; Cancelled: the
/// player stopped it
public enum NfcScanOutcome { Read, NoTag, Failed, Cancelled }

public sealed record NfcScan(NfcScanOutcome Outcome, byte[]? Tag, string? Problem);

/// Looking: waiting for a tag on the controller; Reading: one is there, being read
public enum NfcScanStep { Looking, Reading }

/// What the controller says its NFC reader is doing (the State command's answer)
public sealed record NfcReaderState(int State, int Error, byte[]? Uid)
{
    public const int Idle = 0, Looking = 1, ReadDone = 4, Failed = 7, Found = 9;
    /// The error of a tag that refused the password
    public const int PasswordRefused = 0x48;
}

/// NoController: no Switch 2 Pro Controller is plugged in over USB; InUse: another program holds it; Failed: anything else
public enum NfcReaderProblem { NoController, InUse, Failed }

public sealed class NfcReaderFailure(NfcReaderProblem problem, string message) : Exception(message)
{
    public NfcReaderProblem Problem => problem;
}

/// Reading an NFC tag with a Nintendo Switch 2 Pro Controller plugged in over USB: the commands, their answers, and a scan from
/// "start looking" to the tag's 540 bytes. It only reads: the commands that write a tag (0x08, 0x14) are not here. A command is an
/// 8-byte header, [0x01][0x91][flag][sub][0x00][length of the data][0x00][0x00], then its data; an answer is an 8-byte header, then
/// its payload. Nothing of a tag goes to a log: a problem names the step, never the bytes.
public static class NfcReader
{
    public const byte SubStart = 0x03, SubStop = 0x04, SubState = 0x05, SubRead = 0x06, SubBuffer = 0x15;
    const int Header = 8;
    /// The read's buffer: 60 bytes about the tag, then its pages
    public const int Described = 60;
    /// The most one answer carries of the buffer
    const int LargestPart = 0x59;
    /// How long a tag is waited for, and a read; how often the controller is asked (all in ms)
    public const int LookFor = 10_000, LookAgainFor = 1_000, ReadFor = 2_000, LookEvery = 100, ReadEvery = 50, Settle = 120;

    public static byte[] Command(byte sub, params byte[] data)
    {
        if (sub is 0x08 or 0x14) throw new ArgumentException("This only reads: a command that writes a tag is never sent.", nameof(sub));
        var command = new byte[Header + data.Length];
        (command[0], command[1], command[2], command[3], command[5]) = (0x01, 0x91, (byte)(sub == SubBuffer ? 0x01 : 0x00), sub, (byte)data.Length);
        data.CopyTo(command, Header);
        return command;
    }

    /// Start looking for a tag
    public static byte[] Start() => Command(SubStart, 0x00, 0xE8, 0x03, 0x2C, 0x01);
    public static byte[] Stop() => Command(SubStop);
    public static byte[] State() => Command(SubState);

    /// Read the whole tag (2000 ms at most): password, after logging in with the figure tags' password, which a figure's tag needs
    public static byte[] Read(bool password) =>
        Command(SubRead, 0xD0, 0x07, 0, 0, 0, 0, 0, 0, 0, (byte)(password ? 0x01 : 0x00), 0x03, 0x00, 0x3B, 0x3C, 0x77, 0x78, 0x86, 0x00, 0x00);

    /// The part of the read's buffer at an offset
    public static byte[] Buffer(int offset) => Command(SubBuffer, (byte)(offset & 0xFF), (byte)(offset >> 8 & 0xFF));

    /// The State answer: what the reader is doing, its error when it failed, the UID of the tag it found (7 bytes)
    public static NfcReaderState ParseState(byte[] answer)
    {
        if (answer.Length <= Header) throw new IOException("The controller's answer was too short.");
        var payload = answer.AsSpan(Header);
        var state = payload[0];
        var error = state == NfcReaderState.Failed && payload.Length > 1 ? payload[1] : 0;
        byte[]? uid = null;
        if (state is NfcReaderState.Found or NfcReaderState.ReadDone && payload.Length >= 16 && payload[4] == 0x01 && payload[5] == 0x01 && payload[8] == 0x07)
            uid = payload.Slice(9, 7).ToArray();
        return new NfcReaderState(state, error, uid);
    }

    /// A Buffer answer: its bytes, and whether it is the last part
    public static (byte[] Part, bool Last) ParsePart(byte[] answer)
    {
        if (answer.Length < Header + 3) throw new IOException("The controller's answer was too short.");
        int length = answer[Header + 1];
        if (length > LargestPart || answer.Length < Header + 3 + length) throw new IOException("The controller's answer was cut short.");
        return (answer.AsSpan(Header + 3, length).ToArray(), answer[Header] == 0x01);
    }

    /// The read's whole buffer, part after part from offset 0 until the last one
    public static byte[] ReadBuffer(NfcExchange exchange)
    {
        var buffer = new List<byte>();
        while (true)
        {
            var (part, last) = ParsePart(exchange(Buffer(buffer.Count)));
            buffer.AddRange(part);
            if (last) return buffer.ToArray();
            if (part.Length == 0 || buffer.Count > 4096) throw new IOException("The controller did not finish sending the tag.");
        }
    }

    /// One scan. Stop, start looking, and ask every 100 ms for up to 10 s until a tag is found (started again when the reader went
    /// idle or failed meanwhile); read it with the figure tags' password, and when the tag refuses, find it again and read it
    /// without; fetch the buffer, whose bytes 60 to 600 are the tag; stop. wait: Thread.Sleep in the tests' place
    public static NfcScan Scan(NfcExchange exchange, CancellationToken cancel = default, Action<NfcScanStep>? step = null, Action<int>? wait = null)
    {
        void Pause(int ms)
        {
            cancel.ThrowIfCancellationRequested();
            if (wait != null) wait(ms);
            else cancel.WaitHandle.WaitOne(ms);
            cancel.ThrowIfCancellationRequested();
        }

        void Look()
        {
            exchange(Stop());
            Pause(Settle);
            exchange(Start());
        }

        bool Found(int within)
        {
            for (var waited = 0; waited < within; waited += LookEvery)
            {
                Pause(LookEvery);
                var state = ParseState(exchange(State())).State;
                if (state == NfcReaderState.Found) return true;
                if (state is not (NfcReaderState.Idle or NfcReaderState.Failed)) continue;
                Look();
                waited += Settle;
            }
            return false;
        }

        NfcReaderState ReadTag(bool password)
        {
            exchange(Read(password));
            var state = new NfcReaderState(NfcReaderState.Looking, 0, null);
            for (var waited = 0; waited < ReadFor; waited += ReadEvery)
            {
                Pause(ReadEvery);
                state = ParseState(exchange(State()));
                if (state.State is NfcReaderState.ReadDone or NfcReaderState.Failed) break;
            }
            return state;
        }

        try
        {
            step?.Invoke(NfcScanStep.Looking);
            Look();
            if (!Found(LookFor)) return new NfcScan(NfcScanOutcome.NoTag, null, null);
            step?.Invoke(NfcScanStep.Reading);
            var read = ReadTag(password: true);
            if (read.State == NfcReaderState.Failed)
            {
                Look();
                if (!Found(LookAgainFor)) return new NfcScan(NfcScanOutcome.Failed, null, "the tag was taken away before it was read");
                read = ReadTag(password: false);
            }
            if (read.State != NfcReaderState.ReadDone)
                return new NfcScan(NfcScanOutcome.Failed, null, read.State == NfcReaderState.Failed
                    ? $"the controller could not read the tag (error 0x{read.Error:X2}). Hold it still on the logo"
                    : "the controller did not finish reading the tag. Hold it still on the logo");
            var buffer = ReadBuffer(exchange);
            if (buffer.Length < Described + NfcTags.TagBytes)
                return new NfcScan(NfcScanOutcome.Failed, null, $"the controller read {Math.Max(0, buffer.Length - Described)} of the tag's {NfcTags.TagBytes} bytes");
            return new NfcScan(NfcScanOutcome.Read, buffer.AsSpan(Described, NfcTags.TagBytes).ToArray(), null);
        }
        catch (OperationCanceledException) { return new NfcScan(NfcScanOutcome.Cancelled, null, null); }
        catch (IOException e) { return new NfcScan(NfcScanOutcome.Failed, null, e.Message); }
        finally
        {
            // (the reader is left off whatever happened; a controller that was unplugged meanwhile cannot be told)
            try { exchange(Stop()); } catch (IOException) { }
        }
    }

    /// A scan with the controller plugged into this PC (Windows: NfcReaderUsb). NfcReaderFailure when it cannot be opened
    public static NfcScan ScanUsb(CancellationToken cancel = default, Action<NfcScanStep>? step = null)
    {
        if (!OperatingSystem.IsWindows()) throw new NfcReaderFailure(NfcReaderProblem.Failed, "Scanning a tag needs Windows.");
        using var usb = NfcReaderUsb.Open();
        return Scan(usb.Exchange, cancel, step);
    }

    /// The file a scanned tag is saved as, the name the game gives its own scans: "Scanned 04A1B2C3D4E5F6.bin"
    public static string FileName(byte[] tag) => $"Scanned {NfcTags.Uid(tag)}.bin";

    /// A scanned tag into the NFC folder, as a file of that name is added (NfcTags.AddBytes): refused when it is no figure's tag,
    /// left when the same dump is there, numbered when a different one has its name
    public static NfcTagAdded Save(string folder, byte[] tag) =>
        tag.Length != NfcTags.TagBytes ? new NfcTagAdded("", NfcTagOutcome.Refused, null, Tr.T("it is not an NTAG215 dump"))
        : NfcTags.AddBytes(folder, FileName(tag), tag);
}

/// A Switch 2 Pro Controller over USB on Windows (VID 057E, PID 2069), through WinUSB: its interface 1, which Windows binds to
/// WinUSB, found by its device interface GUID (the one known, else the one its registry key names), opened, and its two bulk
/// pipes: a command out, its answer in
[SupportedOSPlatform("windows")]
public sealed class NfcReaderUsb : IDisposable
{
    const string Device = "VID_057E&PID_2069", Interface = Device + "&MI_01";
    static readonly Guid Known = new("6F13725E-EF0E-4FD3-AE5F-B2DE989EC825");
    const uint Present = 0x02, AllClasses = 0x04, DeviceInterface = 0x10;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareReadWrite = 0x03, OpenExisting = 3, Overlapped = 0x40000000;
    const int AccessDenied = 5, SharingViolation = 32;
    const uint PipeTransferTimeout = 3, Timeout = 500;
    const int Bulk = 2, LargestAnswer = 1024;
    static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct DevInfo
    {
        public uint Size;
        public Guid Class;
        public uint Instance;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData
    {
        public uint Size;
        public Guid Class;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct InterfaceDescriptor
    {
        public byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PipeInfo
    {
        public int Type;
        public byte Id;
        public ushort LargestPacket;
        public byte Interval;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr ClassDevs(IntPtr classGuid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr ClassDevs(ref Guid classGuid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    static extern bool EnumDevice(IntPtr set, uint index, ref DevInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool InstanceId(IntPtr set, ref DevInfo info, char[] id, uint size, out uint needed);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiOpenDevRegKey", SetLastError = true)]
    static extern IntPtr OpenRegKey(IntPtr set, ref DevInfo info, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    static extern bool EnumInterface(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool InterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList", SetLastError = true)]
    static extern bool DestroyList(IntPtr set);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    static extern bool Initialize(SafeFileHandle device, out IntPtr usb);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_Free", SetLastError = true)]
    static extern bool Free(IntPtr usb);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    static extern bool InterfaceSettings(IntPtr usb, byte alternate, out InterfaceDescriptor descriptor);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    static extern bool QueryPipe(IntPtr usb, byte alternate, byte index, out PipeInfo pipe);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    static extern bool SetPipePolicy(IntPtr usb, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_FlushPipe", SetLastError = true)]
    static extern bool FlushPipe(IntPtr usb, byte pipe);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    static extern bool WritePipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint done, IntPtr overlapped);
    [DllImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    static extern bool ReadPipe(IntPtr usb, byte pipe, byte[] buffer, uint length, out uint done, IntPtr overlapped);

    readonly SafeFileHandle file;
    IntPtr usb;
    readonly byte pipeOut, pipeIn;

    NfcReaderUsb(SafeFileHandle file, IntPtr usb, byte pipeOut, byte pipeIn) => (this.file, this.usb, this.pipeOut, this.pipeIn) = (file, usb, pipeOut, pipeIn);

    /// The device interface GUIDs to look under: the known one, then what the registry key of each plugged-in controller's
    /// interface 1 names (DeviceInterfaceGUID or DeviceInterfaceGUIDs, under its Device Parameters)
    static List<Guid> Guids()
    {
        var guids = new List<Guid> { Known };
        var set = ClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, Present | AllClasses);
        if (set == Invalid) return guids;
        try
        {
            var info = new DevInfo { Size = (uint)Marshal.SizeOf<DevInfo>() };
            var id = new char[512];
            for (uint i = 0; EnumDevice(set, i, ref info); i++)
            {
                if (!InstanceId(set, ref info, id, (uint)id.Length, out _)) continue;
                if (!new string(id).TrimEnd('\0').Contains(Interface, StringComparison.OrdinalIgnoreCase)) continue;
                // (DICS_FLAG_GLOBAL, DIREG_DEV, KEY_READ)
                var key = OpenRegKey(set, ref info, 1, 0, 1, 0x20019);
                if (key == Invalid) continue;
                using var parameters = RegistryKey.FromHandle(new SafeRegistryHandle(key, ownsHandle: true));
                var named = parameters.GetValue("DeviceInterfaceGUID") switch { string one => new[] { one }, string[] many => many, _ => Array.Empty<string>() };
                var others = parameters.GetValue("DeviceInterfaceGUIDs") switch { string one => new[] { one }, string[] many => many, _ => Array.Empty<string>() };
                foreach (var text in named.Concat(others))
                    if (Guid.TryParse(text, out var guid) && !guids.Contains(guid)) guids.Add(guid);
            }
        }
        finally { DestroyList(set); }
        return guids;
    }

    /// The paths of the plugged-in controllers' WinUSB interface
    static List<string> Paths()
    {
        var paths = new List<string>();
        foreach (var known in Guids())
        {
            var guid = known;
            var set = ClassDevs(ref guid, null, IntPtr.Zero, Present | DeviceInterface);
            if (set == Invalid) continue;
            try
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                for (uint i = 0; EnumInterface(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    InterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                    if (needed == 0) continue;
                    var detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        // (SP_DEVICE_INTERFACE_DETAIL_DATA_W: its size field, then the path)
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!InterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (path != null && path.Contains(Device, StringComparison.OrdinalIgnoreCase) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            paths.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { DestroyList(set); }
        }
        return paths;
    }

    /// The first controller that opens. NfcReaderFailure: none plugged in; held by another program; or why it did not open
    public static NfcReaderUsb Open()
    {
        var paths = Paths();
        if (paths.Count == 0) throw new NfcReaderFailure(NfcReaderProblem.NoController, "No Switch 2 Pro Controller was found over USB.");
        NfcReaderFailure? failure = null;
        foreach (var path in paths)
        {
            var file = CreateFile(path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            if (file.IsInvalid)
            {
                file.Dispose();
                failure ??= error is AccessDenied or SharingViolation
                    ? new NfcReaderFailure(NfcReaderProblem.InUse, "The controller is in use by another program.")
                    : new NfcReaderFailure(NfcReaderProblem.Failed, $"The controller could not be opened (Windows error {error}).");
                continue;
            }
            if (!Initialize(file, out var usb))
            {
                error = Marshal.GetLastWin32Error();
                file.Dispose();
                failure ??= new NfcReaderFailure(NfcReaderProblem.Failed, $"The controller's USB interface could not be opened (Windows error {error}).");
                continue;
            }
            byte pipeOut = 0, pipeIn = 0;
            if (InterfaceSettings(usb, 0, out var descriptor))
                for (byte i = 0; i < descriptor.Endpoints; i++)
                {
                    if (!QueryPipe(usb, 0, i, out var pipe) || pipe.Type != Bulk) continue;
                    if ((pipe.Id & 0x80) != 0) pipeIn = pipeIn == 0 ? pipe.Id : pipeIn;
                    else pipeOut = pipeOut == 0 ? pipe.Id : pipeOut;
                }
            if (pipeOut == 0 || pipeIn == 0)
            {
                Free(usb);
                file.Dispose();
                failure ??= new NfcReaderFailure(NfcReaderProblem.Failed, "The controller's USB interface has no bulk pipes.");
                continue;
            }
            var timeout = Timeout;
            SetPipePolicy(usb, pipeOut, PipeTransferTimeout, sizeof(uint), ref timeout);
            SetPipePolicy(usb, pipeIn, PipeTransferTimeout, sizeof(uint), ref timeout);
            // (an answer nobody read, left from before)
            FlushPipe(usb, pipeIn);
            return new NfcReaderUsb(file, usb, pipeOut, pipeIn);
        }
        throw failure!;
    }

    /// A command out, its answer in (NfcExchange)
    public byte[] Exchange(byte[] command)
    {
        ObjectDisposedException.ThrowIf(usb == IntPtr.Zero, this);
        if (!WritePipe(usb, pipeOut, command, (uint)command.Length, out var sent, IntPtr.Zero) || sent != command.Length)
            throw new IOException($"the controller did not take the command (Windows error {Marshal.GetLastWin32Error()})");
        var answer = new byte[LargestAnswer];
        if (!ReadPipe(usb, pipeIn, answer, (uint)answer.Length, out var got, IntPtr.Zero))
            throw new IOException($"the controller did not answer (Windows error {Marshal.GetLastWin32Error()})");
        return answer.AsSpan(0, (int)got).ToArray();
    }

    public void Dispose()
    {
        if (usb != IntPtr.Zero) Free(usb);
        usb = IntPtr.Zero;
        file.Dispose();
    }
}
