using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BudisCommander.Core;

/// <summary>
/// Trvale spuštěný příkazový řádek (cmd na Windows, jinak $SHELL) s přesměrovaným vstupem a výstupem.
/// Jde o jednoduchý terminál: příkazy zadávané po řádcích, bez celoobrazovkových programů (vim, top…).
/// </summary>
public sealed class ShellSession : IDisposable
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[()][A-Z0-9]", RegexOptions.Compiled);

    private Process? _process;
    public event Action<string>? Output;
    public event Action<int>? Exited;
    public string Executable { get; }
    public bool Running => _process is { HasExited: false };

    public ShellSession(string? executable = null)
    {
        Executable = executable ?? (OperatingSystem.IsWindows() ? "cmd.exe" : (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s ? s : "/bin/sh"));
    }

    public static string CleanOutput(string text) => Ansi.Replace(text, "").Replace("\r\n", "\n").Replace("\r", "");

    public void Start(string workDir)
    {
        Stop();
        var enc = OperatingSystem.IsWindows() ? Encodings.Oem() : new UTF8Encoding(false);     // bez BOM, jinak by se první příkaz poškodil
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Environment.CurrentDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc, StandardInputEncoding = enc,
        };
        if (!OperatingSystem.IsWindows()) psi.Environment["TERM"] = "dumb";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => { try { Exited?.Invoke(p.ExitCode); } catch { } };
        p.Start();
        _process = p;
        _ = PumpAsync(p.StandardOutput);
        _ = PumpAsync(p.StandardError);
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var text = CleanOutput(new string(buffer, 0, n));
                if (text.Length > 0) Output?.Invoke(text);
            }
        }
        catch { /* proces skončil */ }
    }

    /// <summary>Pošle řádek na vstup příkazového řádku.</summary>
    public void Send(string line)
    {
        if (!Running) return;
        _process!.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
