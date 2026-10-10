namespace BudisCommander.Core;

public sealed partial class AppCore
{
    // --- sady záložek ---------------------------------------------------------------------

    /// <summary>Uloží aktuální záložky obou panelů pod jménem (existující sada stejného jména se přepíše). Vzdálené záložky se vynechají.</summary>
    public void SaveTabSet(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        static (List<string> Paths, int Selected) Snapshot(TabGroup g)
        {
            var paths = new List<string>(); int selected = 0;
            for (int i = 0; i < g.Tabs.Count; i++)
            {
                if (g.Tabs[i].IsRemote || g.Tabs[i].IsArchive) continue;
                if (i == g.Selected) selected = paths.Count;
                paths.Add(g.Tabs[i].PersistentPath);
            }
            return (paths, selected);
        }
        var (l, ls) = Snapshot(Left); var (r, rs) = Snapshot(Right);
        var set = new TabSet { Name = name, Left = l, LeftSelected = ls, Right = r, RightSelected = rs };
        Settings.TabSets.RemoveAll(s => string.Equals(s.Name, name, StringComparison.CurrentCultureIgnoreCase));
        Settings.TabSets.Add(set);
        Settings.TabSets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Settings.Save(_settingsPath);
    }

    public void DeleteTabSet(string name)
    {
        Settings.TabSets.RemoveAll(s => s.Name == name);
        Settings.Save(_settingsPath);
    }

    /// <summary>Otevře sadu záložek v obou panelech; složky, které už neexistují, se vynechají.</summary>
    public async Task LoadTabSetAsync(TabSet set)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (List<string>, int) Fix(List<string> paths, int selected)
        {
            var list = new List<string>(); int sel = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (!Directory.Exists(paths[i])) continue;
                if (i == selected) sel = list.Count;
                list.Add(paths[i]);
            }
            if (list.Count == 0) list.Add(home);
            return (list, sel);
        }
        var (l, ls) = Fix(set.Left, set.LeftSelected);
        var (r, rs) = Fix(set.Right, set.RightSelected);
        Left.Replace(l, ls); Right.Replace(r, rs);
        foreach (var t in Left.Tabs.Concat(Right.Tabs)) Hook(t);
        await Task.WhenAll(Left.Tabs.Concat(Right.Tabs).Select(t => t.LoadLocalAsync(t.Path, "", true)));
        SaveState();
        ShowNotice($"Sada záložek „{set.Name}“ otevřena.");
    }
}
