using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
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
            await Ui.ShowErrorAsync($"Připojení k úložišti „{remote}“ se nezdařilo:\n\n{e.Message}");
            return false;
        }
        SetProgress(null, "");
        await Active.ConnectAsync(session);
        return Active.Connection == session;
    }
}
