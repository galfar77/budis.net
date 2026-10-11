using BudisCommander.Remote;

namespace BudisCommander.Core;

public sealed partial class AppCore
{
    /// <summary>Připojí aktivní panel k Androidu přes adb (soubory v telefonu).</summary>
    public async Task<bool> ConnectPhoneAsync(AdbDevice device)
    {
        var exe = Adb.FindExe(Settings.AdbPath);
        if (exe.Length == 0) { await Ui.ShowErrorAsync("Program adb nebyl nalezen. Nainstalujte ho v okně Telefon."); return false; }
        var session = new AdbSession(exe, device.Serial, device.Model);
        try
        {
            SetProgress(0, "Připojuji " + session.DisplayName + "…");
            await session.ConnectAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            SetProgress(null, "");
            await Ui.ShowErrorAsync($"Připojení k telefonu se nezdařilo:\n\n{e.Message}");
            return false;
        }
        SetProgress(null, "");
        await Active.ConnectAsync(session);
        return Active.Connection == session;
    }
}
