using System.Text.Json;

namespace BudisCommander.Core;

public enum OpKind { Copy, Move, Delete, Rename, NewFolder }

/// <summary>Jedna položka operace: odkud (původní cesta) a kam (nová cesta; u mazání prázdné).</summary>
public sealed record OpItem(string From, string To);

/// <summary>Záznam v historii operací.</summary>
public sealed class OpEntry
{
    public int Id { get; set; }
    public DateTime Time { get; set; }
    public OpKind Kind { get; set; }
    public string Summary { get; set; } = "";
    public List<OpItem> Items { get; set; } = new();
    /// <summary>Mazání natrvalo se obnovit nedá.</summary>
    public bool Permanent { get; set; }
    /// <summary>Operaci už někdo vrátil zpět.</summary>
    public bool Reverted { get; set; }
}

/// <summary>Historie provedených operací se soubory (kopírování, přesuny, mazání…) s uložením do souboru.</summary>
public sealed class OperationLog
{
    public const int MaxEntries = 500;
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly List<OpEntry> _entries = new();
    private int _nextId = 1;
    public event Action? Changed;

    public OperationLog(string? path = null)
    {
        _path = path;
        if (path != null && File.Exists(path))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<OpEntry>>(File.ReadAllText(path));
                if (list != null) { _entries.AddRange(list); _nextId = list.Count == 0 ? 1 : list.Max(e => e.Id) + 1; }
            }
            catch { /* poškozený soubor se ignoruje */ }
        }
    }

    /// <summary>Záznamy od nejnovějšího.</summary>
    public IReadOnlyList<OpEntry> Entries { get { lock (_lock) return _entries.AsEnumerable().Reverse().ToList(); } }

    public OpEntry Add(OpKind kind, string summary, IEnumerable<OpItem> items, bool permanent = false)
    {
        var list = items.ToList();
        OpEntry e;
        lock (_lock)
        {
            e = new OpEntry { Id = _nextId++, Time = DateTime.Now, Kind = kind, Summary = summary, Items = list, Permanent = permanent };
            _entries.Add(e);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        Save();
        Changed?.Invoke();
        return e;
    }

    public void MarkReverted(int id)
    {
        lock (_lock) { var e = _entries.FirstOrDefault(x => x.Id == id); if (e != null) e.Reverted = true; }
        Save(); Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Save(); Changed?.Invoke();
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json; lock (_lock) json = JsonSerializer.Serialize(_entries);
            File.WriteAllText(_path, json);
        }
        catch { /* historie není důležitější než samotná operace */ }
    }

    // --- co jde s operací udělat -------------------------------------------------------------

    public static bool CanRepeat(OpEntry e) => e.Kind == OpKind.Copy && e.Items.Count > 0 && e.Items.All(i => Path.IsPathRooted(i.From) && Path.IsPathRooted(i.To));
    public static bool CanRevert(OpEntry e) => !e.Reverted && e.Items.Count > 0 && e.Kind switch
    {
        OpKind.Copy or OpKind.Move or OpKind.Rename or OpKind.NewFolder => true,
        OpKind.Delete => !e.Permanent,
        _ => false,
    };

    /// <summary>Zopakuje kopírování: každou položku zkopíruje znovu na stejné místo (existující cíl přepíše). Vrací počet zkopírovaných položek.</summary>
    public static async Task<int> RepeatAsync(OpEntry e, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var (from, to) in e.Items)
        {
            if (!Directory.Exists(from) && !File.Exists(from)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            await FileOps.CopyAsync(from, to, true, null, ct);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Vrátí operaci: kopii a novou složku přesune do Koše, přesun a přejmenování vrátí zpět, smazané obnoví z Koše.
    /// Vrací počet vrácených položek a seznam chyb.
    /// </summary>
    public static (int Done, List<string> Errors) Revert(OpEntry e, Func<string, bool>? restoreFromBin = null)
    {
        var done = 0; var errors = new List<string>();
        foreach (var (from, to) in e.Items)
        {
            try
            {
                switch (e.Kind)
                {
                    case OpKind.Copy:
                    case OpKind.NewFolder:
                        if (File.Exists(to) || Directory.Exists(to)) { FileOps.DeleteToRecycleBin(to); done++; }
                        break;
                    case OpKind.Move:
                    case OpKind.Rename:
                        if (File.Exists(from) || Directory.Exists(from)) throw new IOException("původní místo je obsazené");
                        Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                        if (Directory.Exists(to)) Directory.Move(to, from); else File.Move(to, from);
                        done++;
                        break;
                    case OpKind.Delete:
                        if (restoreFromBin == null || !restoreFromBin(from)) throw new FileNotFoundException("v Koši už není");
                        done++;
                        break;
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(from.TrimEnd('\\', '/'))}: {ex.Message}"); }
        }
        return (done, errors);
    }
}
