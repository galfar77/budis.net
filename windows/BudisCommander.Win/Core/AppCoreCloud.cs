using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
    /// <summary>Vytvoří veřejný odkaz na položku pod kurzorem v cloudovém panelu; při chybě ji ukáže a vrátí null.</summary>
    public async Task<string?> CreateShareLinkAsync()
    {
        var pane = Active;
        var item = pane.Current;
        if (pane.Connection is not RcloneSession cloud) { await Ui.ShowErrorAsync("Odkaz jde vytvořit jen v panelu připojeném ke cloudu (menu Servery → Cloudová úložiště)."); return null; }
        if (item == null || item.IsParent) return null;
        try
        {
            Notice = "Vytvářím odkaz…"; StatusChanged?.Invoke();
            return await cloud.LinkAsync(item.FullPath, CancellationToken.None);
        }
        catch (Exception e) { await Ui.ShowErrorAsync("Odkaz se nepodařilo vytvořit (úložiště ho možná neumí):\n\n" + e.Message); return null; }
        finally { Notice = null; StatusChanged?.Invoke(); }
    }

    /// <summary>Připojí aktivní panel k cloudovému úložišti nastavenému v rclone.</summary>
    public async Task<bool> ConnectCloudAsync(string remote)
    {
        var exe = Rclone.FindExe(Settings.RclonePath);
        if (exe == null) { await Ui.ShowErrorAsync("Program rclone nebyl nalezen. Nainstalujte ho (winget install Rclone.Rclone) nebo zadejte jeho cestu."); return false; }
        var session = new RcloneSession(exe, remote);
        try
        {
            SetProgress(0, "Připojuji " + session.DisplayName + "…");
            await session.ConnectAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            SetProgress(null, "");
            session.Dispose();
            if (RcloneSetup.NeedsReconnect(e.Message)
                && await Ui.ConfirmAsync($"Nastavení úložiště „{remote}“ není dokončené", "Chybí výběr disku. Otevře se okno rclone, ve kterém se znovu přihlásíte a disk vyberete (u OneDrive vyberte disk pojmenovaný OneDrive, ne první v seznamu). Potom se k úložišti připojte znovu.", "Opravit"))
            {
                try { RcloneSetup.Reconnect(exe, remote); } catch (Exception ex) { await Ui.ShowErrorAsync(ex.Message); }
                return false;
            }
            await Ui.ShowErrorAsync($"Připojení k úložišti „{remote}“ se nezdařilo:\n\n{e.Message}");
            return false;
        }
        SetProgress(null, "");
        await Active.ConnectAsync(session);
        return Active.Connection == session;
    }
}
