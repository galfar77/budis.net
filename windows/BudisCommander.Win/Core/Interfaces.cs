namespace BudisCommander.Core;

public enum ConflictChoice { Overwrite, OverwriteAll, Skip, Cancel }

/// <summary>Dialogy, které jádro potřebuje od rozhraní (v testech se nahrazuje).</summary>
public interface IUserInterface
{
    Task ShowErrorAsync(string message);
    Task<bool> ConfirmAsync(string title, string info, string okText);
    Task<string?> PromptAsync(string title, string info, string initial, string okText);
    Task<string?> PromptPasswordAsync(string title, string info);
    Task<int> ChooseAsync(string title, string info, string[] buttons);
    Task<ConflictChoice> AskConflictAsync(string name);
    /// <summary>Rozhodne o důvěře klíči SFTP serveru; volá se z vlákna spojení.</summary>
    bool TrustHostKey(string host, string fingerprint);
}

/// <summary>Sada záložek jednoho panelu.</summary>
public sealed class TabGroup
{
    private readonly AppSettings _settings;
    public List<PaneState> Tabs { get; } = new();
    public int Selected { get; private set; }
    public event Action? Changed;

    public TabGroup(AppSettings settings, IEnumerable<string> paths, int selected)
    {
        _settings = settings;
        foreach (var p in paths) Tabs.Add(new PaneState(settings, p));
        if (Tabs.Count == 0) throw new ArgumentException("Aspoň jedna záložka je potřeba.");
        Selected = Math.Clamp(selected, 0, Tabs.Count - 1);
    }

    public PaneState Current => Tabs[Selected];

    /// <summary>Nahradí všechny záložky novými (načtení sady záložek).</summary>
    public void Replace(IEnumerable<string> paths, int selected)
    {
        var fresh = paths.Select(p => new PaneState(_settings, p)).ToList();
        if (fresh.Count == 0) return;
        foreach (var t in Tabs) t.Dispose();
        Tabs.Clear();
        Tabs.AddRange(fresh);
        Selected = Math.Clamp(selected, 0, Tabs.Count - 1);
        Changed?.Invoke();
    }

    public PaneState NewTab()
    {
        var t = new PaneState(_settings, Current.Path);
        Tabs.Insert(Selected + 1, t);
        Selected++;
        Changed?.Invoke();
        return t;
    }

    public void Close(int index)
    {
        if (Tabs.Count <= 1 || index < 0 || index >= Tabs.Count) return;
        Tabs[index].Dispose();
        Tabs.RemoveAt(index);
        if (index < Selected) Selected--;
        Selected = Math.Min(Selected, Tabs.Count - 1);
        Changed?.Invoke();
    }

    public void Select(int index)
    {
        if (index < 0 || index >= Tabs.Count || index == Selected) return;
        Selected = index;
        Changed?.Invoke();
    }

    public void Cycle(int delta) => Select(((Selected + delta) % Tabs.Count + Tabs.Count) % Tabs.Count);
}
