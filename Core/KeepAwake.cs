using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LongshotLauncher.Core;

/// The PC kept from sleeping while a build runs: a build takes 15 to 30 minutes, often hours on a first cold one, and a PC that
/// sleeps at its idle timeout stops it half way. The screen may still turn off; only sleep is held off. Holds are counted: the PC
/// is held from the first Hold to the last one disposed. Windows: a thread of its own holds ES_SYSTEM_REQUIRED (the state is a
/// thread's, so the thread that asked must be the one that lets go). Linux and the Steam Deck: systemd-inhibit holds a sleep and
/// idle lock while its child runs. Nothing is held where neither is there; a failure never stops a build
public static class KeepAwake
{
    static readonly object Gate = new();
    static int holds;
    static IDisposable? held;

    /// What actually holds the PC awake (the tests count it): the platform's own by default
    public static Func<string, IDisposable?> Backend { get; set; } = Platform;

    /// The number of holds not yet let go
    public static int Holds
    {
        get { lock (Gate) return holds; }
    }

    /// The PC held awake until the result is disposed (twice is harmless); why: said to the system where it shows it
    public static IDisposable Hold(string why)
    {
        lock (Gate)
        {
            if (holds++ == 0)
            {
                try { held = Backend(why); }
                catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                                              or DllNotFoundException or EntryPointNotFoundException) { held = null; }
            }
        }
        return new Release();
    }

    sealed class Release : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            IDisposable? letGo = null;
            lock (Gate)
            {
                if (holds > 0 && --holds == 0)
                {
                    letGo = held;
                    held = null;
                }
            }
            try { letGo?.Dispose(); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    static IDisposable? Platform(string why) =>
        OperatingSystem.IsWindows() ? new WindowsHold() : OperatingSystem.IsLinux() ? LinuxHold.Start(why) : null;

    // ---- Windows ----

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    sealed class WindowsHold : IDisposable
    {
        readonly ManualResetEventSlim letGo = new();

        public WindowsHold()
        {
            var thread = new Thread(() =>
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                letGo.Wait();
                SetThreadExecutionState(EsContinuous);
            })
            { IsBackground = true, Name = "Longshot keep awake" };
            thread.Start();
        }

        public void Dispose() => letGo.Set();
    }

    // ---- Linux ----

    sealed class LinuxHold : IDisposable
    {
        readonly Process process;

        LinuxHold(Process process) => this.process = process;

        /// systemd-inhibit holding the lock while its child runs: cat, reading the launcher's end of a pipe, so the lock goes with the
        /// launcher however it ends (a crash too: the pipe closes, cat ends); null without systemd-inhibit
        public static LinuxHold? Start(string why)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("systemd-inhibit")
                {
                    ArgumentList = { "--what=sleep:idle", "--who=Longshot Launcher", "--why=" + why, "--mode=block", "cat" },
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                });
                return p == null ? null : new LinuxHold(p);
            }
            catch (System.ComponentModel.Win32Exception) { return null; }   // (no systemd-inhibit: nothing held)
        }

        public void Dispose()
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) { }
            process.Dispose();
        }
    }
}
