using System.Diagnostics;

namespace BudisCommander.Core;

public sealed record ProcInfo(int Pid, string Name, long MemoryBytes, int Threads, string Title);

/// <summary>Seznam běžících procesů a jejich ukončení (správce procesů).</summary>
public static class ProcessList
{
    public static List<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string title = "";
                try { title = p.MainWindowTitle; } catch { }
                list.Add(new ProcInfo(p.Id, p.ProcessName, p.WorkingSet64, p.Threads.Count, title));
            }
            catch { /* proces mezitím skončil nebo je nepřístupný */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static IEnumerable<ProcInfo> Filter(IEnumerable<ProcInfo> all, string text, bool byMemory)
    {
        var q = all.Where(p => string.IsNullOrWhiteSpace(text)
            || p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString() == text.Trim()
            || p.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
        return byMemory ? q.OrderByDescending(p => p.MemoryBytes) : q.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid);
    }

    /// <summary>Ukončí proces (se všemi potomky, když <paramref name="tree"/>); vrací chybu nebo null.</summary>
    public static string? Kill(int pid, bool tree)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (tree) p.Kill(true);
            else if (!p.CloseMainWindow()) p.Kill();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }
}
