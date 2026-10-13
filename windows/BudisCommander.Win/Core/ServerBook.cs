namespace BudisCommander.Core;

/// <summary>Uložené servery: zobrazované názvy, seskupení do složek a zapamatování s ohledem na ručně spravované položky.</summary>
public static class ServerBook
{
    public const int MaxUnmanaged = 20;

    /// <summary>Ručně pojmenovaný nebo zařazený do skupiny (takové položky se nikdy samy nemažou).</summary>
    public static bool IsManaged(SavedServer s) => !string.IsNullOrWhiteSpace(s.Name) || !string.IsNullOrWhiteSpace(s.Group);

    public static string DisplayName(SavedServer s) => string.IsNullOrWhiteSpace(s.Name) ? s.Title : $"{s.Name.Trim()}   ({s.Title})";

    /// <summary>Servery bez skupiny jako první, pak skupiny podle abecedy; uvnitř skupiny podle názvu.</summary>
    public static List<(string Group, List<SavedServer> Servers)> Tree(IEnumerable<SavedServer> servers)
    {
        return servers
            .GroupBy(s => (s.Group ?? "").Trim(), StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key.Length == 0 ? 0 : 1).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (g.Key, g.OrderBy(s => DisplayName(s), StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
    }

    public static List<string> Groups(IEnumerable<SavedServer> servers) =>
        servers.Select(s => (s.Group ?? "").Trim()).Where(g => g.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)
               .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// Zapamatuje server po připojení nebo uložení: stejný server (protokol, uživatel, adresa, port) se nahradí na svém místě
    /// a zůstane mu název i skupina; nový jde na začátek. Nespravované položky se ořežou na <see cref="MaxUnmanaged"/>.
    /// </summary>
    public static void Remember(List<SavedServer> list, SavedServer server)
    {
        var i = list.FindIndex(s => s.Title == server.Title);
        if (i >= 0)
        {
            var old = list[i];
            if (string.IsNullOrWhiteSpace(server.Name)) server.Name = old.Name;
            if (string.IsNullOrWhiteSpace(server.Group)) server.Group = old.Group;
            if (server.ProtectedPassword == null && old.ProtectedPassword != null && server.KeepPassword) server.ProtectedPassword = old.ProtectedPassword;
            list[i] = server;
        }
        else list.Insert(0, server);
        var unmanaged = list.Where(s => !IsManaged(s)).ToList();
        foreach (var extra in unmanaged.Skip(MaxUnmanaged)) list.Remove(extra);
    }

    public static SavedServer Duplicate(SavedServer s) => new()
    {
        Protocol = s.Protocol, Host = s.Host, Port = s.Port, User = s.User, Insecure = s.Insecure, KeyPath = s.KeyPath,
        ProtectedPassword = s.ProtectedPassword, Group = s.Group, Name = (string.IsNullOrWhiteSpace(s.Name) ? s.Host : s.Name) + " (kopie)",
    };
}
