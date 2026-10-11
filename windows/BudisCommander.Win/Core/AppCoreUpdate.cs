namespace BudisCommander.Core;

public sealed partial class AppCore
{
    private string _stagedCommit = "";
    private Timer? _updateTimer;

    /// <summary>Při běhu aplikace hledá novou verzi každé 3 hodiny (s automatickou instalací i ji nainstaluje).</summary>
    public void StartUpdateTimer() =>
        _updateTimer ??= new Timer(_ => _ = AutoCheckUpdateAsync(), null, TimeSpan.FromHours(3), TimeSpan.FromHours(3));

    // --- aktualizace aplikace ----------------------------------------------------------------

    /// <summary>Tiše zjistí, jestli je venku nová verze (při startu, nejvýš jednou denně) a jen to oznámí ve stavovém řádku.</summary>
    public async Task AutoCheckUpdateAsync(HttpClient? http = null, string? currentCommit = null, string? exePath = null)
    {
        // S automatickou instalací se kontroluje častěji (nejvýš jednou za hodinu), jinak jednou za 20 hodin.
        var interval = Settings.AutoInstallUpdates ? 1 : 20;
        if (!Settings.AutoCheckUpdates || (DateTime.UtcNow - Settings.LastUpdateCheck).TotalHours < interval) return;
        currentCommit ??= Updater.CurrentCommit;
        if (currentCommit.Length == 0) return;                    // vývojové sestavení
        try
        {
            using var own = http == null ? Updater.NewClient() : null;
            var info = await Updater.CheckAsync(http ?? own!, currentCommit, Updater.AssetNameForThisMachine);
            Settings.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save(_settingsPath);
            if (info == null) return;
            var exe = exePath ?? Updater.SelfExePath();
            if (Settings.AutoInstallUpdates && exe != null)
            {
                if (_stagedCommit == info.Commit) return;           // tahle verze už je nainstalovaná a čeká na restart
                var client = http ?? own!;
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "budis-update-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var zip = await Updater.DownloadAsync(client, info, dir, null);
                    Updater.Apply(zip, exe);
                    _stagedCommit = info.Commit;
                    ShowNotice("Nová verze je nainstalovaná, projeví se po restartu aplikace. Předchozí verzi vrátíte v menu Nápověda.");
                }
                catch (Exception e) { ShowNotice("Automatická instalace aktualizace se nezdařila: " + e.Message); }
                finally { try { Directory.Delete(dir, true); } catch { } }
                return;
            }
            ShowNotice("Je dostupná nová verze Budis Commanderu. Nainstalujete ji v menu Nápověda → Aktualizovat aplikaci.");
        }
        catch { /* bez internetu se nic neděje */ }
    }

    /// <summary>Vrátí předchozí verzi aplikace. Vrací true, když se výměna povedla (je potřeba restart).</summary>
    public async Task<bool> RollBackAsync(string? exePath = null)
    {
        exePath ??= Updater.SelfExePath();
        if (exePath == null) { await Ui.ShowErrorAsync("Tuto aplikaci nelze vyměnit za sebe samu (nespustili jste samostatný BudisCommander.exe)."); return false; }
        if (!Updater.CanRollBack(exePath)) { await Ui.ShowErrorAsync("Předchozí verze není k dispozici. Uloží se při příští aktualizaci."); return false; }
        if (!await Ui.ConfirmAsync("Vrátit předchozí verzi?", "Teď nainstalovaná verze se uloží a půjde na ni přepnout zpět stejným příkazem.", "Vrátit")) return false;
        try { Updater.RollBack(exePath); return true; }
        catch (Exception e) { await Ui.ShowErrorAsync("Návrat se nezdařil:\n\n" + e.Message); return false; }
    }

    /// <summary>Zeptá se na novou verzi, stáhne ji a vymění .exe. Vrací true, když je potřeba aplikaci restartovat.</summary>
    public async Task<bool> UpdateAsync(HttpClient? http = null, string? currentCommit = null, string? exePath = null, string? workDir = null)
    {
        currentCommit ??= Updater.CurrentCommit;
        exePath ??= Updater.SelfExePath();
        UpdateInfo? info;
        using var own = http == null ? Updater.NewClient() : null;
        var client = http ?? own!;
        try
        {
            Notice = "Zjišťuji, jestli je nová verze…"; StatusChanged?.Invoke();
            info = await Updater.CheckAsync(client, currentCommit, Updater.AssetNameForThisMachine);
        }
        catch (Exception e)
        {
            Notice = null; StatusChanged?.Invoke();
            await Ui.ShowErrorAsync("Kontrola aktualizací se nezdařila (je počítač online?):\n\n" + e.Message);
            return false;
        }
        Notice = null; StatusChanged?.Invoke();
        Settings.LastUpdateCheck = DateTime.UtcNow;
        if (info == null) { ShowNotice("Máte nejnovější verzi."); return false; }
        if (exePath == null)
        {
            await Ui.ShowErrorAsync("Nová verze existuje, ale tuto aplikaci nelze vyměnit za sebe samu (nespustili jste samostatný BudisCommander.exe).\n\nStáhněte ji ze stránky Releases na GitHubu.");
            return false;
        }
        if (!await Ui.ConfirmAsync("Je dostupná nová verze", $"Stáhne se {Formatting.Size(info.Size)} a aplikace se po výměně restartuje.", "Aktualizovat")) return false;
        try
        {
            var dir = workDir ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "budis-update-" + Guid.NewGuid().ToString("N"));
            var progress = new Progress<double>(f => SetProgress(f, "Stahuji aktualizaci…"));
            var zip = await Updater.DownloadAsync(client, info, dir, progress);
            SetProgress(null, "");
            Updater.Apply(zip, exePath);
            try { Directory.Delete(dir, true); } catch { }
            return true;
        }
        catch (Exception e)
        {
            SetProgress(null, "");
            await Ui.ShowErrorAsync("Aktualizace se nezdařila:\n\n" + e.Message);
            return false;
        }
    }
}
