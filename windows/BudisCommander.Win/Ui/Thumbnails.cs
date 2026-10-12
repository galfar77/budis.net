using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BudisCommander.Ui;

/// <summary>Miniatury souborů ze Průzkumníka (obrázky, videa, PDF, dokumenty…) přes IShellItemImageFactory; jen ve Windows.</summary>
public static class Thumbnails
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly SemaphoreSlim Gate = new(3);
    public const int Size = 32;

    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>Miniatura souboru, nebo null, když ji systém nemá (pak zůstane ikona). Výsledky se pamatují podle cesty, velikosti a data změny.</summary>
    public static async Task<Bitmap?> GetAsync(string path, long size, DateTime? modified)
    {
        if (!Available) return null;
        var key = $"{path}|{size}|{modified?.Ticks}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(key, out hit)) return hit;
            var bmp = await Task.Run(() => LoadShell(path));
            if (Cache.Count > 800) Cache.Clear();
            Cache[key] = bmp;
            return bmp;
        }
        finally { Gate.Release(); }
    }

    // --- COM -----------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int cb, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[]? bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SIIGBF_THUMBNAILONLY = 0x8;

    private static Bitmap? LoadShell(string path)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), out var factory);
            try
            {
                if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_THUMBNAILONLY, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                try { return ToBitmap(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; }
    }

    private static Bitmap? ToBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // miniatura bez průhlednosti má alfa kanál samé nuly
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        }
        return bmp;
    }
}
