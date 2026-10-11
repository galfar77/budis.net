using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BudisCommander.Core.Terminal;

/// <summary>
/// Příkazový řádek ve skutečném (pseudo)terminálu: ve Windows přes ConPTY, jinde přes příkaz script
/// (jen pro testy a vývoj). Výstup se předává jako text v UTF-8, vstup se zapisuje beze změny.
/// </summary>
public sealed class PtySession : IDisposable
{
    public event Action<string>? Output;
    public event Action<int>? Exited;
    public string Executable { get; }
    public bool Running { get; private set; }
    /// <summary>Poslední chyba při čtení výstupu nebo při jeho zpracování (pro hledání potíží).</summary>
    public string? LastError { get; private set; }

    private Stream? _in, _out;
    private Process? _proc;                        // jen mimo Windows
    private IntPtr _hpc;
    private IntPtr _procHandle;
    private SafeFileHandle? _inWrite, _outRead;
    private int _cols = 80, _rows = 24;
    private volatile bool _disposed;
    private int _exitRaised;

    public PtySession(string? executable = null)
    {
        Executable = executable ?? (OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/sh");
    }

    public void Start(string workDir, int cols, int rows, string? args = null)
    {
        Stop();
        _exitRaised = 0;
        _cols = Math.Max(2, cols); _rows = Math.Max(2, rows);
        if (!Directory.Exists(workDir)) workDir = Environment.CurrentDirectory;
        if (OperatingSystem.IsWindows()) StartConPty(workDir, args); else StartScript(workDir, args);
        Running = true;
    }

    public void Write(string text)
    {
        if (!Running || _in == null) return;
        try { var b = new UTF8Encoding(false).GetBytes(text); _in.Write(b, 0, b.Length); _in.Flush(); } catch { }
    }

    public void Resize(int cols, int rows)
    {
        _cols = Math.Max(2, cols); _rows = Math.Max(2, rows);
        if (!Running) return;
        if (OperatingSystem.IsWindows() && _hpc != IntPtr.Zero) ResizePseudoConsole(_hpc, new COORD { X = (short)_cols, Y = (short)_rows });
    }

    public void Stop()
    {
        Running = false;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc?.Dispose(); _proc = null;
        if (_procHandle != IntPtr.Zero) { try { TerminateProcess(_procHandle, 0); } catch { } CloseHandle(_procHandle); _procHandle = IntPtr.Zero; }
        if (_hpc != IntPtr.Zero) { var h = _hpc; _hpc = IntPtr.Zero; Task.Run(() => { try { ClosePseudoConsole(h); } catch { } }); }
        try { _in?.Dispose(); } catch { }
        try { _out?.Dispose(); } catch { }
        _in = _out = null; _inWrite = _outRead = null;
    }

    public void Dispose() { _disposed = true; Stop(); }

    private void Pump(Stream s, Func<int> exitCode)
    {
        var th = new Thread(() =>
        {
            var dec = Encoding.UTF8.GetDecoder();
            var buf = new byte[8192]; var chars = new char[8192];
            try
            {
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    var c = dec.GetChars(buf, 0, n, chars, 0);
                    if (c > 0 && !_disposed)
                    {
                        try { Output?.Invoke(new string(chars, 0, c)); }
                        catch (Exception e) { LastError = "Zpracování výstupu: " + e; }
                    }
                }
            }
            catch (Exception e) { LastError = "Čtení výstupu: " + e.Message; }
            RaiseExited(exitCode);
        }) { IsBackground = true, Name = "pty-reader" };
        th.Start();
    }

    /// <summary>Ohlásí konec programu právě jednou (ve Windows ho zjistí čekání na proces, jinde konec výstupu).</summary>
    private void RaiseExited(Func<int> exitCode)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        Running = false;
        if (_disposed) return;
        try { Exited?.Invoke(exitCode()); } catch { }
    }

    // ---------------------------------------------------------------- mimo Windows (test, vývoj)

    private void StartScript(string workDir, string? args)
    {
        var cmd = Executable + (args is { Length: > 0 } ? " " + args : "");
        var psi = new ProcessStartInfo("script")
        {
            WorkingDirectory = workDir, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (OperatingSystem.IsMacOS()) { psi.ArgumentList.Add("-q"); psi.ArgumentList.Add("/dev/null"); psi.ArgumentList.Add("/bin/sh"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(cmd); }
        else { psi.ArgumentList.Add("-qfec"); psi.ArgumentList.Add($"stty cols {_cols} rows {_rows} 2>/dev/null; {cmd}"); psi.ArgumentList.Add("/dev/null"); }
        psi.Environment["TERM"] = "xterm-256color";
        var p = Process.Start(psi) ?? throw new InvalidOperationException("script");
        _proc = p; _in = p.StandardInput.BaseStream; _out = p.StandardOutput.BaseStream;
        Pump(_out, () => { try { p.WaitForExit(2000); return p.ExitCode; } catch { return -1; } });
    }

    // ---------------------------------------------------------------- Windows: ConPTY

    private void StartConPty(string workDir, string? args)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        var hr = CreatePseudoConsole(new COORD { X = (short)_cols, Y = (short)_rows }, inRead, outWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");
        inRead.Dispose(); outWrite.Dispose();                      // konce, které patří konzoli
        _inWrite = inWrite; _outRead = outRead;

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrs = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var si = new STARTUPINFOEX { lpAttributeList = attrs };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            var cmdLine = new StringBuilder(args is { Length: > 0 } ? $"\"{Executable}\" {args}" : $"\"{Executable}\"");
            if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, workDir, ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.hThread);
            _procHandle = pi.hProcess;
        }
        finally { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }

        _in = new FileStream(_inWrite, FileAccess.Write, 1, false);
        _out = new FileStream(_outRead, FileAccess.Read, 1, false);
        var ph = _procHandle;
        int ExitCode() { try { return GetExitCodeProcess(ph, out var code) ? code : -1; } catch { return -1; } }
        Pump(_out, ExitCode);
        // ConPTY nezavře výstupní rouru, dokud se nezavře konzole, proto se na konec programu čeká přímo na proces
        new Thread(() =>
        {
            WaitForSingleObject(ph, 0xFFFFFFFF);
            Thread.Sleep(150);                                  // ať se doplní poslední výstup
            RaiseExited(ExitCode);
        }) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out int code);
}
