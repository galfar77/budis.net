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
| F3 / F4 | zobrazit / editovat |
| F5 / F6 | kopírovat / přesunout do druhého panelu |
| F7 / F8 | nový adresář / do koše |
| ⌘R, ⌘A, ⌘., ⌘U | obnovit, označit vše, skryté soubory, zrcadlit adresář |

Na Macu je u F-kláves potřeba držet `fn` (nebo vypnout „Používat klávesy F1, F2 jako standardní funkční klávesy“); spodní lištu lze také klikat.
