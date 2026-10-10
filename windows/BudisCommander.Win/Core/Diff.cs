namespace BudisCommander.Core;

public enum DiffKind { Same, Removed, Added }

public sealed record DiffRow(DiffKind Kind, string? Left, string? Right, int? LeftNo, int? RightNo);

public abstract record DiffResult
{
    public sealed record Rows(List<DiffRow> Lines, int Changes) : DiffResult;
    public sealed record Binary(bool Identical) : DiffResult;
    public sealed record TooLarge(bool Identical) : DiffResult;
}

public static class DiffEngine
{
    public const int MaxBytes = 3 * 1024 * 1024;
    public const int MaxLines = 8000;
    private const int MaxEdits = 4000;

    public static DiffResult Compute(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length > MaxBytes || fb.Length > MaxBytes) return new DiffResult.TooLarge(LocalFs.SameContent(a, b));
        var da = File.ReadAllBytes(a); var db = File.ReadAllBytes(b);
        if (LocalFs.LooksBinary(da) || LocalFs.LooksBinary(db)) return new DiffResult.Binary(da.AsSpan().SequenceEqual(db));

        var left = SplitLines(System.Text.Encoding.UTF8.GetString(da));
        var right = SplitLines(System.Text.Encoding.UTF8.GetString(db));
        if (left.Count > MaxLines || right.Count > MaxLines) return new DiffResult.TooLarge(da.AsSpan().SequenceEqual(db));

        var script = Myers(left, right);
        if (script == null) return new DiffResult.TooLarge(da.AsSpan().SequenceEqual(db));

        var rows = new List<DiffRow>();
        int i = 0, j = 0, changes = 0;
        foreach (var op in script)
        {
            switch (op)
            {
                case 0: rows.Add(new DiffRow(DiffKind.Same, left[i], right[j], i + 1, j + 1)); i++; j++; break;
                case -1: rows.Add(new DiffRow(DiffKind.Removed, left[i], null, i + 1, null)); i++; changes++; break;
                default: rows.Add(new DiffRow(DiffKind.Added, null, right[j], null, j + 1)); j++; changes++; break;
            }
        }
        return new DiffResult.Rows(rows, changes);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);   // závěrečný konec řádku není řádek navíc
        return lines;
    }

    /// <summary>Myersův algoritmus: vrací posloupnost operací 0 = shodné, -1 = smazáno, 1 = přidáno.</summary>
    public static List<int>? Myers(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count, max = n + m;
        if (max == 0) return new List<int>();
        var trace = new List<int[]>();
        var v = new int[2 * max + 2];
        int offset = max;
        for (int d = 0; d <= max; d++)
        {
            if (d > MaxEdits) return null;
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) x = v[offset + k + 1];
                else x = v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, d, offset);
            }
        }
        return null;
    }

    private static List<int> Backtrack(List<int[]> trace, IReadOnlyList<string> a, IReadOnlyList<string> b, int dEnd, int offset)
    {
        var ops = new List<int>();
        int x = a.Count, y = b.Count;
        for (int d = dEnd; d > 0; d--)
        {
            var v = trace[d];
            int k = x - y;
            int prevK = (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1])) ? k + 1 : k - 1;
            int prevX = v[offset + prevK];
            int prevY = prevX - prevK;
            while (x > prevX && y > prevY) { ops.Add(0); x--; y--; }
            ops.Add(x == prevX ? 1 : -1);
            x = prevX; y = prevY;
        }
        while (x > 0 && y > 0) { ops.Add(0); x--; y--; }
        ops.Reverse();
        return ops;
    }
}
