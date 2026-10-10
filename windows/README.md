# Budis Commander pro Windows

Dvoupanelový správce souborů pro Windows 10 a 11 (C#, .NET 8, Avalonia). Funkčně odpovídá verzi pro macOS ve složce `Sources`.

## Jak ho získat

**A) Z GitHubu (nic neinstalujete):** otevřete stránku [Releases](../../releases/tag/windows-latest) a stáhněte ZIP.
Jsou tam dva: `BudisCommander-win-x64.zip` (běžné počítače) a `BudisCommander-win-arm64.zip` (Windows na ARM,
např. Parallels Desktop na Macu s Apple Silicon). Rozbalte a spusťte `BudisCommander.exe`.

**B) Vlastní sestavení** (potřebujete .NET 8 SDK: `winget install Microsoft.DotNet.SDK.8`):

```powershell
cd windows
.\build.ps1          # x64
.\build.ps1 arm64    # Windows na ARM
```

Windows může při prvním spuštění zobrazit SmartScreen (aplikace není podepsaná): *Další informace → Přesto spustit*.

## Ovládání

Zkratky jsou stejné jako u Total Commanderu, kde to jde, a jdou změnit v **Nastavení** (Ctrl+,) i vypsat v menu.

| Klávesa | Akce |
|---|---|
| Tab | přepnout panel |
| ↑ ↓ PgUp PgDn Home End | pohyb |
| Enter / Backspace | otevřít / o úroveň výš (zip a další archivy se otevřou jako složka) |
| Space, Insert, Shift+↑/↓ | označit položku |
| Num + / Num − / Num * | označit / odznačit podle masky / převrátit označení |
| psaní písmen | rychlé hledání; **Ctrl+F** filtr panelu |
| F2 / F3 / F4 | přejmenovat / zobrazit (text, hex, obrázek, PDF) / editovat |
| F5 / F6 | kopírovat / přesunout do druhého panelu (fronta, zrušení, pokračování) |
| F7 / F8, Delete | nová složka / smazat do Koše (Shift+Delete natrvalo) |
| Alt+F5 / Alt+F9 | zabalit (zip, tar.gz) / rozbalit (zip, tar, 7z, rar…) |
| Alt+F7 | hledat soubory (název, obsah; i na serveru) |
| Alt+Enter, Alt+Shift+Enter | atributy a časy, velikosti složek |
| Ctrl+Z | vrátit poslední operaci |
| Ctrl+C / X / V | schránka souborů (kompatibilní s Průzkumníkem), přetahování myší funguje také |
| Ctrl+T / W / Tab | záložky |
| Ctrl+Q, Ctrl+B, Ctrl+J | rychlý náhled, všechny podsložky najednou, příkazový řádek |
| Ctrl+D, Alt+←/→ | oblíbené a poslední složky, historie |
| Ctrl+M | hromadné přejmenování |
| Ctrl+N | připojit k FTP / FTP+TLS / SFTP (heslo jde uložit, je šifrované přes DPAPI) |
| Ctrl+Shift+N | síť a disky, sdílené složky `\\server\sdileni` |
| Ctrl+Shift+D / Y | porovnat adresáře / zrcadlit do druhého panelu (funguje i mezi složkou a serverem, v obou směrech) |
| Ctrl+Shift+B | sady záložek: uložit rozložení panelů pod jménem a otevřít ho jedním klikem |
| Ctrl+Shift+I / H | porovnat dva soubory / kontrolní součty |

Další nástroje jsou v menu *Nástroje* a *Soubor*: hledání duplicit, rozdělení a slepení souborů, přidání do zipu,
symbolické odkazy, uživatelské příkazy (menu a lišta dole).

**Cloud (Dropbox, Google Drive, OneDrive…):** menu *Servery → Cloudová úložiště (rclone)*. Potřebujete program
[rclone](https://rclone.org) (`winget install Rclone.Rclone`); úložiště se nastaví jednou v okně přes tlačítko
*Nastavit úložiště…* (spustí `rclone config`). Pak je cloud v panelu jako obyčejný server.

**Aktualizace:** menu *Nástroje → Aktualizovat aplikaci* stáhne z GitHubu novou verzi a vymění `BudisCommander.exe`
(při startu aplikace nejvýš jednou denně jen oznámí, že nová verze existuje; jde vypnout v Nastavení).
**Zástupci:** *Nástroje → Vytvořit zástupce na ploše a v nabídce Start*.
**Jazyk a vzhled:** čeština nebo angličtina a světlý/tmavý/podle systému v *Nastavení* (jazyk po restartu);
vzhled jde přepnout i z menu *Zobrazit*.

Nastavení a stav se ukládají do `%APPDATA%\BudisCommander\settings.json`.

## Vývoj a testy

```powershell
dotnet test BudisCommander.Tests            # všechny testy
```

Integrační testy vzdálených serverů se spustí jen při nastavených proměnných `BUDIS_TEST_FTP` a `BUDIS_TEST_SFTP`
(`host:port:uživatel:heslo`).
