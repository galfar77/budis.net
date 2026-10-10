using System.Diagnostics;
using System.Text;

namespace BudisCommander.Core;

/// <summary>Spouští příkazy v příkazovém řádku Windows a předává jejich výstup.</summary>
public sealed class CommandRunner
{
    private Process? _process;
    public bool Running => _process is { HasExited: false };

    public void Run(string commandLine, string workDir, Action<string> onOutput, Action<int> onExit)
    {
        if (Running) return;
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = OperatingSystem.IsWindows() ? Encodings.Oem() : Encoding.UTF8,
            StandardErrorEncoding = OperatingSystem.IsWindows() ? Encodings.Oem() : Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(commandLine); }
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(commandLine); }

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onOutput(e.Data + "\n"); };
        try
        {
            p.Start();
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _process = p;
        }
        catch (Exception e)
        {
            onOutput(e.Message + "\n");
            onExit(-1);
            return;
        }
        // konec se hlásí až po přečtení celého výstupu
        _ = Task.Run(async () =>
        {
            await p.WaitForExitAsync();
            p.WaitForExit();
            var code = p.ExitCode;
            _process = null;
            onExit(code);
        });
    }

    public void Cancel()
    {
        try { _process?.Kill(true); } catch { }
    }
}
