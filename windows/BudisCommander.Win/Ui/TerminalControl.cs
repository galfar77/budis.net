using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BudisCommander.Core.Terminal;

namespace BudisCommander.Ui;

/// <summary>Vykresluje obrazovku emulátoru terminálu a posílá stisky kláves do příkazového řádku (ConPTY).</summary>
public sealed class TerminalControl : Control
{
    private readonly TerminalBuffer _buf = new(80, 24);
    private readonly PtySession _pty = new();
    private readonly Typeface _typeface = new("Cascadia Mono, Consolas, Menlo, monospace");
    private readonly Typeface _bold = new("Cascadia Mono, Consolas, Menlo, monospace", FontStyle.Normal, FontWeight.Bold);
    private double _cw = 8, _ch = 16, _fontSize = 14;
    private int _offset;                                    // odrolování do historie
    private (int row, int col)? _selA, _selB;               // výběr: absolutní řádek a sloupec
    private bool _selecting;
    private string _workDir = Environment.CurrentDirectory;
    private int _lastVersion = -1;
    private bool _pending;
    private readonly StringWriterQueue _queue = new();

    public static readonly IBrush DefaultFg = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    public static readonly IBrush DefaultBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly uint[] Ansi16 =
    {
        0x000000, 0xCD3131, 0x0DBC79, 0xE5E510, 0x2472C8, 0xBC3FBC, 0x11A8CD, 0xE5E5E5,
        0x666666, 0xF14C4C, 0x23D18B, 0xF5F543, 0x3B8EEA, 0xD670D6, 0x29B8DB, 0xFFFFFF,
    };

    public string Title => _buf.Title;
    public bool Running => _pty.Running;
    public event Action? TitleOrStateChanged;
    public TerminalBuffer Buffer => _buf;

    public TerminalControl()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        MeasureFonts();
        _buf.Reply += s => _pty.Write(s);
        _pty.Output += text => { _queue.Add(text); if (!_pending) { _pending = true; Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render); } };
        _pty.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            _buf.Write("\r\n\x1b[2m" + BudisCommander.Core.Tr.T($"[proces skončil, kód {code}; Restartovat v liště nahoře]") + "\x1b[0m\r\n");
            TitleOrStateChanged?.Invoke(); InvalidateVisual();
        });
    }

    private void Flush()
    {
        _pending = false;
        var text = _queue.TakeAll();
        if (text.Length == 0) return;
        _buf.Write(text);
        _offset = 0;
        TitleOrStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void Start(string workDir)
    {
        _workDir = workDir;
        ResizeToBounds();
        try { _pty.Start(workDir, _buf.Cols, _buf.Rows); }
        catch (Exception e) { _buf.Write("Nepodařilo se spustit příkazový řádek: " + e.Message + "\r\n"); }
        TitleOrStateChanged?.Invoke();
        InvalidateVisual();
    }

    public void Restart() { _pty.Stop(); _buf.Write("\x1bc"); Start(_workDir); }
    public void Stop() => _pty.Dispose();
    public void SendText(string text) { _offset = 0; _pty.Write(text); }

    private void MeasureFonts()
    {
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        _cw = Math.Max(1, ft.Width / 10); _ch = Math.Max(1, Math.Ceiling(ft.Height));
    }

    private void ResizeToBounds()
    {
        if (Bounds.Width < 10 || Bounds.Height < 10) return;
        var cols = Math.Max(2, (int)(Bounds.Width / _cw)); var rows = Math.Max(2, (int)(Bounds.Height / _ch));
        if (cols == _buf.Cols && rows == _buf.Rows) return;
        _buf.Resize(cols, rows);
        _pty.Resize(cols, rows);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); ResizeToBounds(); InvalidateVisual(); }

    // ---------------------------------------------------------------- vykreslení

    public static Color ColorOf(int code, Color fallback)
    {
        if (code < 0) return fallback;
        if (code >= 0x1000000) return Color.FromRgb((byte)(code >> 16), (byte)(code >> 8), (byte)code);
        if (code < 16) { var v = Ansi16[code]; return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v); }
        if (code < 232)
        {
            var c = code - 16; int Lv(int i) => i == 0 ? 0 : 55 + i * 40;
            return Color.FromRgb((byte)Lv(c / 36), (byte)Lv(c / 6 % 6), (byte)Lv(c % 6));
        }
        var g = (byte)(8 + (code - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    public override void Render(DrawingContext ctx)
    {
        var fg0 = ((SolidColorBrush)DefaultFg).Color; var bg0 = ((SolidColorBrush)DefaultBg).Color;
        ctx.FillRectangle(DefaultBg, new Rect(Bounds.Size));
        for (var row = 0; row < _buf.Rows; row++)
        {
            var line = _buf.ViewLine(row, _offset);
            var y = row * _ch;
            var abs = _buf.ScrollbackCount - _offset + row;
            var x = 0;
            while (x < line.Length)
            {
                // souvislý úsek se stejnými atributy
                var c0 = line[x]; var end = x + 1;
                while (end < line.Length && line[end].Fg == c0.Fg && line[end].Bg == c0.Bg && line[end].Attr == c0.Attr && Selected(abs, end) == Selected(abs, x)) end++;
                var inv = (c0.Attr & 16) != 0 != Selected(abs, x);
                var fgCode = inv ? c0.Bg : c0.Fg; var bgCode = inv ? c0.Fg : c0.Bg;
                var fgColor = inv && fgCode < 0 ? bg0 : ColorOf(fgCode, fg0);
                var bgColor = inv && bgCode < 0 ? fg0 : ColorOf(bgCode, bg0);
                if (bgColor != bg0) ctx.FillRectangle(new SolidColorBrush(bgColor), new Rect(x * _cw, y, (end - x) * _cw + 0.5, _ch));
                var text = new string(Enumerable.Range(x, end - x).Select(i => line[i].Ch == '\0' ? ' ' : line[i].Ch).ToArray());
                if (text.Trim().Length > 0)
                {
                    if ((c0.Attr & 2) != 0) fgColor = Color.FromArgb(160, fgColor.R, fgColor.G, fgColor.B);
                    var bold = (c0.Attr & 1) != 0;
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _bold : _typeface, _fontSize, new SolidColorBrush(fgColor));
                    ctx.DrawText(ft, new Point(x * _cw, y));
                }
                if ((c0.Attr & 8) != 0) ctx.DrawLine(new Pen(new SolidColorBrush(fgColor), 1), new Point(x * _cw, y + _ch - 1.5), new Point(end * _cw, y + _ch - 1.5));
                x = end;
            }
        }
        if (_buf.CursorVisible && _offset == 0)
        {
            var r = new Rect(_buf.CursorX * _cw, _buf.CursorY * _ch, _cw, _ch);
            if (IsKeyboardFocusWithin) ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), r);
            else ctx.DrawRectangle(new Pen(Brushes.White, 1), r.Deflate(0.5));
        }
    }

    // ---------------------------------------------------------------- klávesnice

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control); var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt); var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && shift && e.Key == Key.C || ctrl && e.Key == Key.Insert) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V || shift && e.Key == Key.Insert) { _ = PasteAsync(); e.Handled = true; return; }
        if (shift && e.Key == Key.PageUp) { Scroll(_buf.Rows - 1); e.Handled = true; return; }
        if (shift && e.Key == Key.PageDown) { Scroll(-(_buf.Rows - 1)); e.Handled = true; return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        string? seq = _buf.KeySequence(e.Key switch { Key.Back => "Backspace", Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown", _ => e.Key.ToString() }, ctrl, alt, shift);
        if (seq == null && ctrl && !alt)
        {
            if (e.Key >= Key.A && e.Key <= Key.Z) seq = ((char)(e.Key - Key.A + 1)).ToString();
            else if (e.Key == Key.OemOpenBrackets) seq = "\x1b";
            else if (e.Key == Key.OemBackslash || e.Key == Key.OemPipe) seq = "\x1c";
            else if (e.Key == Key.OemCloseBrackets) seq = "\x1d";
            else if (e.Key == Key.Space) seq = "\0";
        }
        if (seq == null && alt && !ctrl && e.Key >= Key.A && e.Key <= Key.Z)
            seq = "\x1b" + (char)((shift ? 'A' : 'a') + (e.Key - Key.A));
        if (seq != null) { SendText(seq); e.Handled = true; ClearSelection(); }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text)) { SendText(e.Text); e.Handled = true; ClearSelection(); }
    }

    // ---------------------------------------------------------------- myš, výběr, schránka

    private (int row, int col) CellAt(Point p)
    {
        var col = Math.Clamp((int)(p.X / _cw), 0, _buf.Cols - 1);
        var row = Math.Clamp((int)(p.Y / _ch), 0, _buf.Rows - 1);
        return (_buf.ScrollbackCount - _offset + row, col);
    }

    private bool Selected(int absRow, int col)
    {
        if (_selA is not { } a || _selB is not { } b) return false;
        var (s, e) = (a.row, a.col).CompareTo((b.row, b.col)) <= 0 ? (a, b) : (b, a);
        return (absRow, col).CompareTo((s.row, s.col)) >= 0 && (absRow, col).CompareTo((e.row, e.col)) <= 0;
    }

    private void ClearSelection() { if (_selA != null) { _selA = _selB = null; InvalidateVisual(); } }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed) { if (_selA != null) CopySelection(); else _ = PasteAsync(); ClearSelection(); return; }
        if (pt.Properties.IsMiddleButtonPressed) { _ = PasteAsync(); return; }
        _selA = _selB = CellAt(pt.Position); _selecting = true; e.Pointer.Capture(this); InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_selecting) return;
        _selB = CellAt(e.GetPosition(this)); InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false; e.Pointer.Capture(null);
        if (_selA == _selB) ClearSelection();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_buf.InAltScreen) { SendText(e.Delta.Y > 0 ? "\x1b[A\x1b[A\x1b[A" : "\x1b[B\x1b[B\x1b[B"); e.Handled = true; return; }
        Scroll((int)Math.Round(e.Delta.Y * 3)); e.Handled = true;
    }

    private void Scroll(int lines)
    {
        _offset = Math.Clamp(_offset + lines, 0, _buf.ScrollbackCount);
        InvalidateVisual();
    }

    public string SelectedText()
    {
        if (_selA is not { } a || _selB is not { } b) return "";
        var (s, e) = (a.row, a.col).CompareTo((b.row, b.col)) <= 0 ? (a, b) : (b, a);
        var sb = new System.Text.StringBuilder();
        for (var r = s.row; r <= e.row; r++)
        {
            var line = _buf.AbsLine(r);
            var from = r == s.row ? s.col : 0; var to = r == e.row ? e.col : line.Length - 1;
            var part = new string(Enumerable.Range(from, Math.Max(0, to - from + 1)).Where(i => i < line.Length).Select(i => line[i].Ch == '\0' ? ' ' : line[i].Ch).ToArray());
            sb.Append(r == e.row ? part : part.TrimEnd() + "\n");
        }
        return sb.ToString();
    }

    private void CopySelection()
    {
        var text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        var text = await cb.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        SendText(_buf.BracketedPaste ? "\x1b[200~" + text + "\x1b[201~" : text);
    }

    /// <summary>Malá fronta textu mezi vláknem čtení a vláknem rozhraní.</summary>
    private sealed class StringWriterQueue
    {
        private readonly object _lock = new(); private System.Text.StringBuilder _sb = new();
        public void Add(string s) { lock (_lock) _sb.Append(s); }
        public string TakeAll() { lock (_lock) { var s = _sb.ToString(); _sb.Clear(); return s; } }
    }
}
