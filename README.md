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
| psaní písmen | rychlé hledání (skok na první položku začínající textem) |
| Alt + znak | filtr panelu: ukáže jen položky obsahující text (bez ohledu na velikost písmen a diakritiku); pokračuje se psaním, Backspace maže, Esc zruší |
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
| ⌘P / ⌘E | zabalit (.zip s volitelným heslem, .tar.gz) / rozbalit archiv (i zip s heslem) |
| ⌘F | hledat soubory podle názvu a obsahu |
| ⌘, | nastavení: vzhled, písmo, klávesové zkratky |
| ⌘Y | zrcadlit adresář do druhého panelu (nové a změněné zkopíruje, přebytečné v cíli přesune do koše; s potvrzením) |
| ⌘J | příkazový řádek ve složce aktivního panelu |
| ⌘B | oblíbené a poslední složky |
| ⌘[ / ⌘] | zpět / vpřed v historii panelu |
| ⌘I | porovnat dva soubory (kurzor v každém panelu) řádek po řádku |
| ⌘H | kontrolní součty MD5, SHA-1, SHA-256 (+ ověření, uložení .sha256) |
| ⌘G | všechny podsložky najednou (ploché zobrazení, Backspace zpět) |
| ⌘S | spočítat velikosti složek (ve sloupci Velikost místo ‹DIR›); trvale: Nastavení → Automaticky počítat velikosti složek |
| ⌘O | atributy: datum změny, práva, skrytý příznak (i rekurzivně) |
| ⌘N | panel rychlého náhledu místo neaktivního panelu |
| ⌘R, ⌘A, ⌘., ⌘U | obnovit, označit vše, skryté soubory, zrcadlit adresář |

Na Macu je u F-kláves potřeba držet `fn` (nebo vypnout „Používat klávesy F1, F2 jako standardní funkční klávesy“); spodní lištu lze také klikat.

## Hotová aplikace

`./scripts/make-app.sh` vyrobí univerzální `build/BudisCommander.app` (Apple Silicon i Intel) včetně ikony
a ZIP. Na GitHubu ji staví také workflow *Build macOS app* (záložka Actions → artefakt `BudisCommander-macOS`).

Aplikace je podepsaná jen ad hoc, takže ji macOS u staženého souboru zablokuje. Otevřete ji pravým tlačítkem →
Otevřít, nebo spusťte `xattr -dr com.apple.quarantine BudisCommander.app`.

Ikonu lze znovu vygenerovat příkazem `python3 scripts/make-icon.py` (potřebuje Pillow).

Všechny ⌘ zkratky jdou změnit v Nastavení (⌘,) a jsou také v menu **Nástroje**.

## Další funkce

- **Archivy jako složky:** Enter na zipu, tar, tgz, 7z… ho otevře v panelu (jen pro čtení, soubory z něj kopírujte F5). Backspace se vrátí.
- **Náhled F3:** text, obrázky (png, jpg, heic…) a PDF; tlačítkem Tisk… (⌘P) se dají vytisknout.
- **Zrušení přenosu:** během kopírování, mazání a zrcadlení je ve stavovém řádku tlačítko Zrušit.
- **Hledání na serveru** (⌘F v panelu připojeném k serveru) podle názvu.

## Podepsání a notarizace

Skript `make-app.sh` umí podepsat aplikaci a poslat ji k notarizaci, pokud máte placený účet Apple Developer
(proměnné `SIGN_IDENTITY` a `NOTARY_PROFILE`, viz komentář ve skriptu). Bez nich se podepisuje jen ad hoc.

## Další nástroje (menu Nástroje)

- **Porovnat adresáře podle obsahu**, **rozdělit** a **slepit** soubory (díly name.001, name.002…), **symbolický odkaz**.
- **Miniatury**, samostatný **sloupec přípony** (Nastavení).
- **Hexový výpis** binárních souborů v F3.
- **Fronta přenosů:** další F5/F6 během běžícího přenosu se zařadí do fronty; přerušený přenos jde dokončit příkazem *Pokračovat v přerušeném přenosu*.
- **Uživatelské příkazy** (menu a tlačítková lišta dole): vlastní shellové příkazy se zástupnými znaky %f, %n, %d, %o, %F.

## Pohodlí a práce se soubory (další vlna)

- **Živé obnovování:** panely se samy aktualizují, když se obsah složky změní (i mimo aplikaci).
- **Přetahování:** soubory jdou táhnout mezi panely, do složek v panelu a z/do Finderu (aplikace se zeptá, zda kopírovat nebo přesunout). Funguje i do panelu připojeného k serveru.
- **Schránka:** ⌘C, ⌘X, ⌘V pracují se soubory (kompatibilní s Finderem). Menu Nástroje: kopírovat cestu, název, cestu složky.
- **Vrátit (⌘Z):** přejmenování, nová složka, kopírování a přesun v lokálních složkách a smazání do koše. Maže se z koše, takže jde vrátit i vícenásobně (až 30 kroků).
- **Systémový Quick Look:** Shift+Space.
- **Duplicity:** menu Nástroje → Hledat duplicity (podle obsahu, volitelně i druhý panel), výběr přebytečných a do koše.
- **Sloupce Práva, Vlastník, Rozměry/délka** a **barevné štítky Finderu** (Nastavení; štítky nastavíte z menu Nástroje → Štítky Finderu…).
- **Zip:** zabalit s heslem, přidat soubory do existujícího zipu (kurzor v druhém panelu na archivu), rozbalit zip s heslem.
