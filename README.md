# Budis Commander

Dvoupanelový správce souborů pro macOS (SwiftUI, macOS 14+).

## Spuštění

```sh
swift run                 # rychlé spuštění z terminálu
./scripts/make-app.sh     # složí build/BudisCommander.app
```

Nebo v Xcode: `File ▸ Open…` a vybrat složku projektu (Package.swift), pak ⌘R.

## Ovládání

| Klávesa | Akce |
|---|---|
| Tab | přepnout panel |
| ↑ ↓ PgUp PgDn Home End | pohyb kurzoru |
| Enter / → | otevřít adresář či soubor |
| Backspace / ← | o úroveň výš |
| Space, Shift+↑/↓ | označit položku |
| + / - / * | označit / odznačit podle masky / označit vše |
| psaní písmen | rychlé hledání |
| F2 | přejmenovat |
| F3 | zobrazit |
| F4 | editovat (na serveru se změny po uložení samy nahrají zpět) |
| F5 / F6 | kopírovat / přesunout do druhého panelu |
| F7 / F8 | nový adresář / do koše |
| ⌘K | připojit k FTP / FTP+TLS / SFTP serveru |
| ⌘L | síť a disky: SMB počítače v LAN, připojení sdílených složek (smb://, afp://, nfs://) |
| ⌘T / ⌘W / ⌘1–9, Ctrl+Tab | nová / zavřít / přepnout záložku |
| ⌘D | porovnat adresáře (označí rozdílné, novější a chybějící; pak F5) |
| ⌘M | hromadné přejmenování (maska, hledat/nahradit, regulární výrazy, čítač) |
| ⌘Z / ⌘E | zabalit (.zip, .tar.gz) / rozbalit archiv |
| ⌘F | hledat soubory podle názvu a obsahu |
| ⌘, | nastavení: vzhled, písmo, klávesové zkratky |
| ⌘R, ⌘A, ⌘., ⌘U | obnovit, označit vše, skryté soubory, zrcadlit adresář |

Na Macu je u F-kláves potřeba držet `fn` (nebo vypnout „Používat klávesy F1, F2 jako standardní funkční klávesy“); spodní lištu lze také klikat.

## Hotová aplikace

`./scripts/make-app.sh` vyrobí univerzální `build/BudisCommander.app` (Apple Silicon i Intel) včetně ikony
a ZIP. Na GitHubu ji staví také workflow *Build macOS app* (záložka Actions → artefakt `BudisCommander-macOS`).

Aplikace je podepsaná jen ad hoc, takže ji macOS u staženého souboru zablokuje. Otevřete ji pravým tlačítkem →
Otevřít, nebo spusťte `xattr -dr com.apple.quarantine BudisCommander.app`.

Ikonu lze znovu vygenerovat příkazem `python3 scripts/make-icon.py` (potřebuje Pillow).

Všechny ⌘ zkratky jdou změnit v Nastavení (⌘,) a jsou také v menu **Nástroje**.
