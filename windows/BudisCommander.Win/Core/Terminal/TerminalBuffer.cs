using System.Text;

namespace BudisCommander.Core.Terminal;

/// <summary>Jedna buňka obrazovky terminálu. Barvy: -1 výchozí, 0–255 paleta xterm, 0x1000000|RGB pravá barva.</summary>
public struct Cell
{
    public char Ch;
    public int Fg, Bg;
    public byte Attr;       // 1 tučně, 2 tlumeně, 4 kurzíva, 8 podtržení, 16 inverzně
    public static Cell Blank(int fg = -1, int bg = -1) => new() { Ch = ' ', Fg = fg, Bg = bg };
}

/// <summary>
/// Emulátor terminálu VT100/xterm: přijímá text s řídicími sekvencemi a udržuje obrazovku, kurzor, historii a režimy.
/// Šířka všech znaků se bere jako jedna buňka (široké znaky CJK se nezdvojují).
/// </summary>
public sealed class TerminalBuffer
{
    public const int MaxScrollback = 5000;
    private Cell[][] _main, _alt, _screen;
    private readonly List<Cell[]> _scrollback = new();
    private bool _usingAlt;
    private int _top, _bottom;                     // oblast posouvání
    private int _savedX, _savedY, _altSavedX, _altSavedY;
    private bool _wrapPending;
    private int _fg = -1, _bg = -1; private byte _attr;
    private int _savedFg = -1, _savedBg = -1; private byte _savedAttr;
    private readonly StringBuilder _seq = new();
    private enum St { Ground, Esc, Csi, Osc, OscEsc, Charset, Dcs }
    private St _st;
    private bool[] _tabs;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool InAltScreen => _usingAlt;
    public string Title { get; private set; } = "";
    public int Version { get; private set; }               // zvyšuje se při každé změně, ovládací prvek podle něj překresluje
    public event Action<string>? Reply;                    // odpovědi, které se mají poslat zpět programu (např. poloha kurzoru)
    public int ScrollbackCount => _scrollback.Count;

    public TerminalBuffer(int cols = 80, int rows = 24)
    {
        Cols = Math.Max(2, cols); Rows = Math.Max(2, rows);
        _main = NewScreen(Cols, Rows); _alt = NewScreen(Cols, Rows); _screen = _main;
        _top = 0; _bottom = Rows - 1;
        _tabs = new bool[Cols]; ResetTabs();
    }

    private static Cell[][] NewScreen(int cols, int rows)
    {
        var s = new Cell[rows][];
        for (var i = 0; i < rows; i++) s[i] = NewLine(cols);
        return s;
    }
    private static Cell[] NewLine(int cols, int fg = -1, int bg = -1) { var l = new Cell[cols]; Array.Fill(l, Cell.Blank(fg, bg)); return l; }
    private void ResetTabs() { for (var i = 0; i < _tabs.Length; i++) _tabs[i] = i % 8 == 0; }

    public Cell Get(int x, int y) => y >= 0 && y < Rows && x >= 0 && x < Cols ? _screen[y][x] : Cell.Blank();

    /// <summary>Řádek podle absolutního čísla: 0 je nejstarší řádek historie, ScrollbackCount je první řádek obrazovky.</summary>
    public Cell[] AbsLine(int idx)
    {
        if (idx < 0) return NewLine(Cols);
        if (idx < _scrollback.Count)
        {
            var l = _scrollback[idx];
            if (l.Length == Cols) return l;
            var c = NewLine(Cols); Array.Copy(l, c, Math.Min(l.Length, Cols)); return c;
        }
        var r = idx - _scrollback.Count;
        return r < Rows ? _screen[r] : NewLine(Cols);
    }

    /// <summary>Řádek v pohledu; scrollOffset je počet řádků, o které je pohled odrolovaný do historie.</summary>
    public Cell[] ViewLine(int row, int scrollOffset) => AbsLine(_scrollback.Count - scrollOffset + row);

    public string GetLineText(int y)
    {
        var sb = new StringBuilder();
        for (var x = 0; x < Cols; x++) sb.Append(_screen[y][x].Ch);
        return sb.ToString().TrimEnd();
    }

    public string GetScreenText() => string.Join("\n", Enumerable.Range(0, Rows).Select(GetLineText)).TrimEnd('\n');

    // ---------------------------------------------------------------- změna velikosti

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols); rows = Math.Max(2, rows);
        if (cols == Cols && rows == Rows) return;
        // hlavní obrazovka: ztracené horní řádky putují do historie
        var ns = NewScreen(cols, rows);
        var oldMain = _main;
        var keepFrom = 0;
        if (!_usingAlt && CursorY >= rows) keepFrom = CursorY - rows + 1;
        for (var i = 0; i < keepFrom; i++) PushScrollback(oldMain[i]);
        for (var y = 0; y < rows && y + keepFrom < oldMain.Length; y++)
            Array.Copy(oldMain[y + keepFrom], ns[y], Math.Min(oldMain[y + keepFrom].Length, cols));
        _main = ns;
        _alt = NewScreen(cols, rows);
        if (!_usingAlt) CursorY -= keepFrom;
        _screen = _usingAlt ? _alt : _main;
        Cols = cols; Rows = rows;
        _top = 0; _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1); CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _tabs = new bool[cols]; ResetTabs(); _wrapPending = false;
        Version++;
    }

    private void PushScrollback(Cell[] line)
    {
        _scrollback.Add((Cell[])line.Clone());
        if (_scrollback.Count > MaxScrollback) _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
    }

    // ---------------------------------------------------------------- vstup

    public void Write(string text)
    {
        foreach (var ch in text) Feed(ch);
        Version++;
    }

    private void Feed(char c)
    {
        switch (_st)
        {
            case St.Ground: Ground(c); break;
            case St.Esc: EscChar(c); break;
            case St.Csi:
                if (c >= '@' && c <= '~') { _st = St.Ground; Csi(_seq.ToString(), c); }
                else if (c == 0x18 || c == 0x1A) _st = St.Ground;
                else if (c < 0x20) Ground(c);                    // řídicí znaky uvnitř sekvence se provedou
                else _seq.Append(c);
                break;
            case St.Osc:
                if (c == '\a') { EndOsc(); }
                else if (c == '\x1b') _st = St.OscEsc;
                else _seq.Append(c);
                break;
            case St.OscEsc:
                if (c == '\\') EndOsc();
                else { _st = St.Esc; EscChar(c); }
                break;
            case St.Charset: _st = St.Ground; break;
            case St.Dcs:
                if (c == '\x1b') _st = St.OscEsc;
                else if (c == '\a') _st = St.Ground;
                break;
        }
    }

    private void EndOsc()
    {
        _st = St.Ground;
        var s = _seq.ToString();
        var semi = s.IndexOf(';');
        if (semi > 0 && (s[..semi] == "0" || s[..semi] == "2")) Title = s[(semi + 1)..];
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b': _st = St.Esc; break;
            case '\r': CursorX = 0; _wrapPending = false; break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
            case '\t': TabForward(1); break;
            case '\a': break;
            case '\0': break;
            default:
                if (c < 0x20 || c == 0x7f) break;
                Put(c); break;
        }
    }

    private void Put(char c)
    {
        if (_wrapPending)
        {
            if (AutoWrap) { CursorX = 0; LineFeed(); }
            _wrapPending = false;
        }
        _screen[CursorY][CursorX] = new Cell { Ch = c, Fg = _fg, Bg = _bg, Attr = _attr };
        if (CursorX >= Cols - 1) _wrapPending = true; else CursorX++;
    }

    private void LineFeed()
    {
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int n)
    {
        for (var i = 0; i < n; i++)
        {
            if (!_usingAlt && _top == 0) PushScrollback(_screen[0]);
            for (var y = _top; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewLine(Cols, -1, _bg);
        }
    }

    private void ScrollDown(int n)
    {
        for (var i = 0; i < n; i++)
        {
            for (var y = _bottom; y > _top; y--) _screen[y] = _screen[y - 1];
            _screen[_top] = NewLine(Cols, -1, _bg);
        }
    }

    private void TabForward(int n)
    {
        for (var i = 0; i < n; i++)
        {
            do CursorX++; while (CursorX < Cols - 1 && !_tabs[CursorX]);
            if (CursorX >= Cols - 1) { CursorX = Cols - 1; break; }
        }
        _wrapPending = false;
    }

    private void EscChar(char c)
    {
        _st = St.Ground;
        switch (c)
        {
            case '[': _st = St.Csi; _seq.Clear(); break;
            case ']': _st = St.Osc; _seq.Clear(); break;
            case 'P': case '_': case '^': case 'X': _st = St.Dcs; break;
            case '(': case ')': case '*': case '+': _st = St.Charset; break;
            case '7': _savedX = CursorX; _savedY = CursorY; _savedFg = _fg; _savedBg = _bg; _savedAttr = _attr; break;
            case '8': CursorX = Math.Min(_savedX, Cols - 1); CursorY = Math.Min(_savedY, Rows - 1); _fg = _savedFg; _bg = _savedBg; _attr = _savedAttr; _wrapPending = false; break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < _tabs.Length) _tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
            case '\x1b': _st = St.Esc; break;
            default: break;     // =, >, # a další se ignorují
        }
    }

    private void FullReset()
    {
        _main = NewScreen(Cols, Rows); _alt = NewScreen(Cols, Rows); _screen = _main; _usingAlt = false;
        _scrollback.Clear(); _top = 0; _bottom = Rows - 1; CursorX = CursorY = 0; _fg = _bg = -1; _attr = 0;
        CursorVisible = true; AppCursorKeys = false; BracketedPaste = false; AutoWrap = true; _wrapPending = false; ResetTabs();
    }

    // ---------------------------------------------------------------- CSI

    private static List<int> Params(string s, out string prefix)
    {
        prefix = "";
        var i = 0;
        while (i < s.Length && (s[i] == '?' || s[i] == '>' || s[i] == '=' || s[i] == '<' || s[i] == '!')) i++;
        prefix = s[..i];
        var res = new List<int>();
        foreach (var part in s[i..].Split(';'))
        {
            // mezery a mezilehlé znaky (např. u DECSCUSR) se ignorují
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            res.Add(digits.Length == 0 ? -1 : int.Parse(digits.Length > 6 ? digits[..6] : digits));
        }
        return res;
    }

    private static int P(List<int> p, int i, int def) => i < p.Count && p[i] > 0 ? p[i] : def;

    private void Csi(string seq, char final)
    {
        var p = Params(seq, out var prefix);
        if (final is not ('m' or 'h' or 'l')) _wrapPending = false;
        if (prefix == "?" )
        {
            if (final is 'h' or 'l') { foreach (var m in p) SetPrivateMode(m, final == 'h'); }
            return;
        }
        if (prefix.Length > 0)
        {
            if (final == 'c' && prefix == ">") Reply?.Invoke("\x1b[>0;0;0c");
            return;
        }
        switch (final)
        {
            case 'A': CursorY = Math.Max(_top >= CursorY ? 0 : _top, CursorY - P(p, 0, 1)); break;
            case 'B': CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + P(p, 0, 1)); break;
            case 'C': CursorX = Math.Min(Cols - 1, CursorX + P(p, 0, 1)); break;
            case 'D': CursorX = Math.Max(0, CursorX - P(p, 0, 1)); break;
            case 'E': CursorX = 0; CursorY = Math.Min(Rows - 1, CursorY + P(p, 0, 1)); break;
            case 'F': CursorX = 0; CursorY = Math.Max(0, CursorY - P(p, 0, 1)); break;
            case 'G': case '`': CursorX = Math.Clamp(P(p, 0, 1) - 1, 0, Cols - 1); break;
            case 'd': CursorY = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1); break;
            case 'H': case 'f': CursorY = Math.Clamp(P(p, 0, 1) - 1, 0, Rows - 1); CursorX = Math.Clamp(P(p, 1, 1) - 1, 0, Cols - 1); break;
            case 'I': TabForward(P(p, 0, 1)); break;
            case 'J': EraseDisplay(p.Count > 0 && p[0] > 0 ? p[0] : 0); break;
            case 'K': EraseLine(p.Count > 0 && p[0] > 0 ? p[0] : 0); break;
            case 'L': if (CursorY >= _top && CursorY <= _bottom) InsertLines(P(p, 0, 1)); break;
            case 'M': if (CursorY >= _top && CursorY <= _bottom) DeleteLines(P(p, 0, 1)); break;
            case '@': InsertChars(P(p, 0, 1)); break;
            case 'P': DeleteChars(P(p, 0, 1)); break;
            case 'X': { var n = P(p, 0, 1); for (var x = CursorX; x < Math.Min(Cols, CursorX + n); x++) _screen[CursorY][x] = Cell.Blank(-1, _bg); break; }
            case 'S': ScrollUp(P(p, 0, 1)); break;
            case 'T': ScrollDown(P(p, 0, 1)); break;
            case 'm': Sgr(p); break;
            case 'r':
                {
                    var t = P(p, 0, 1) - 1; var b = P(p, 1, Rows) - 1;
                    if (t < b && b < Rows) { _top = t; _bottom = b; CursorX = 0; CursorY = 0; }
                    break;
                }
            case 's': _savedX = CursorX; _savedY = CursorY; break;
            case 'u': CursorX = Math.Min(_savedX, Cols - 1); CursorY = Math.Min(_savedY, Rows - 1); break;
            case 'n':
                if (p.Count > 0 && p[0] == 6) Reply?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R");
                else if (p.Count > 0 && p[0] == 5) Reply?.Invoke("\x1b[0n");
                break;
            case 'c': Reply?.Invoke("\x1b[?1;2c"); break;
            case 'g': if (p.Count > 0 && p[0] == 3) Array.Clear(_tabs); else if (CursorX < _tabs.Length) _tabs[CursorX] = false; break;
            case 'h': case 'l': break;
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 7: AutoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47: case 1047: SwitchScreen(on, false); break;
            case 1049: SwitchScreen(on, true); break;
        }
    }

    private void SwitchScreen(bool alt, bool saveCursor)
    {
        if (alt == _usingAlt) return;
        if (alt)
        {
            if (saveCursor) { _altSavedX = CursorX; _altSavedY = CursorY; }
            _usingAlt = true; _screen = _alt = NewScreen(Cols, Rows);
            _top = 0; _bottom = Rows - 1;
        }
        else
        {
            _usingAlt = false; _screen = _main;
            _top = 0; _bottom = Rows - 1;
            if (saveCursor) { CursorX = Math.Min(_altSavedX, Cols - 1); CursorY = Math.Min(_altSavedY, Rows - 1); }
        }
        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                for (var x = CursorX; x < Cols; x++) _screen[CursorY][x] = Cell.Blank(-1, _bg);
                for (var y = CursorY + 1; y < Rows; y++) _screen[y] = NewLine(Cols, -1, _bg);
                break;
            case 1:
                for (var y = 0; y < CursorY; y++) _screen[y] = NewLine(Cols, -1, _bg);
                for (var x = 0; x <= CursorX && x < Cols; x++) _screen[CursorY][x] = Cell.Blank(-1, _bg);
                break;
            case 2: for (var y = 0; y < Rows; y++) _screen[y] = NewLine(Cols, -1, _bg); break;
            case 3: _scrollback.Clear(); break;
        }
    }

    private void EraseLine(int mode)
    {
        var from = mode == 0 ? CursorX : 0; var to = mode == 1 ? CursorX : Cols - 1;
        for (var x = from; x <= to && x < Cols; x++) _screen[CursorY][x] = Cell.Blank(-1, _bg);
    }

    private void InsertLines(int n)
    {
        n = Math.Min(n, _bottom - CursorY + 1);
        for (var i = 0; i < n; i++)
        {
            for (var y = _bottom; y > CursorY; y--) _screen[y] = _screen[y - 1];
            _screen[CursorY] = NewLine(Cols, -1, _bg);
        }
        CursorX = 0;
    }

    private void DeleteLines(int n)
    {
        n = Math.Min(n, _bottom - CursorY + 1);
        for (var i = 0; i < n; i++)
        {
            for (var y = CursorY; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewLine(Cols, -1, _bg);
        }
        CursorX = 0;
    }

    private void InsertChars(int n)
    {
        var line = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        for (var x = Cols - 1; x >= CursorX + n; x--) line[x] = line[x - n];
        for (var x = CursorX; x < CursorX + n; x++) line[x] = Cell.Blank(-1, _bg);
    }

    private void DeleteChars(int n)
    {
        var line = _screen[CursorY];
        n = Math.Min(n, Cols - CursorX);
        for (var x = CursorX; x < Cols - n; x++) line[x] = line[x + n];
        for (var x = Cols - n; x < Cols; x++) line[x] = Cell.Blank(-1, _bg);
    }

    private void Sgr(List<int> p)
    {
        for (var i = 0; i < p.Count; i++)
        {
            var v = p[i] < 0 ? 0 : p[i];
            switch (v)
            {
                case 0: _fg = _bg = -1; _attr = 0; break;
                case 1: _attr |= 1; break;
                case 2: _attr |= 2; break;
                case 3: _attr |= 4; break;
                case 4: _attr |= 8; break;
                case 7: _attr |= 16; break;
                case 22: _attr = (byte)(_attr & ~(1 | 2)); break;
                case 23: _attr = (byte)(_attr & ~4); break;
                case 24: _attr = (byte)(_attr & ~8); break;
                case 27: _attr = (byte)(_attr & ~16); break;
                case >= 30 and <= 37: _fg = v - 30; break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = v - 40; break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = v - 90 + 8; break;
                case >= 100 and <= 107: _bg = v - 100 + 8; break;
                case 38 or 48:
                    {
                        int color = -1;
                        if (i + 2 < p.Count && p[i + 1] == 5) { color = Math.Clamp(p[i + 2], 0, 255); i += 2; }
                        else if (i + 4 < p.Count && p[i + 1] == 2) { color = 0x1000000 | (Math.Clamp(p[i + 2], 0, 255) << 16) | (Math.Clamp(p[i + 3], 0, 255) << 8) | Math.Clamp(p[i + 4], 0, 255); i += 4; }
                        if (color >= 0) { if (v == 38) _fg = color; else _bg = color; }
                        break;
                    }
            }
        }
    }

    // ---------------------------------------------------------------- vstup z klávesnice

    /// <summary>Sekvence, kterou má terminál poslat programu pro danou klávesu (null = klávesa se řeší jako text).</summary>
    public string? KeySequence(string key, bool ctrl, bool alt, bool shift)
    {
        string? csi(char final, string? mod = null)
        {
            var m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            if (m > 1) return $"\x1b[1;{m}{final}";
            return AppCursorKeys ? $"\x1bO{final}" : $"\x1b[{final}";
        }
        string tilde(int n)
        {
            var m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
            return m > 1 ? $"\x1b[{n};{m}~" : $"\x1b[{n}~";
        }
        return key switch
        {
            "Up" => csi('A'), "Down" => csi('B'), "Right" => csi('C'), "Left" => csi('D'),
            "Home" => csi('H'), "End" => csi('F'),
            "Insert" => tilde(2), "Delete" => tilde(3), "PageUp" => tilde(5), "PageDown" => tilde(6),
            "F1" => "\x1bOP", "F2" => "\x1bOQ", "F3" => "\x1bOR", "F4" => "\x1bOS",
            "F5" => tilde(15), "F6" => tilde(17), "F7" => tilde(18), "F8" => tilde(19),
            "F9" => tilde(20), "F10" => tilde(21), "F11" => tilde(23), "F12" => tilde(24),
            "Enter" => "\r", "Tab" => shift ? "\x1b[Z" : "\t", "Back" or "Backspace" => alt ? "\x1b\x7f" : "\x7f", "Escape" => "\x1b",
            _ => null,
        };
    }
}
