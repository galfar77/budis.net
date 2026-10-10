using System.Globalization;
using System.Text;

namespace BudisCommander.Core;

/// <summary>Registrace starších znakových sad (Windows-1250, OEM 852…), které .NET jinak nezná.</summary>
public static class Encodings
{
    private static bool _done;

    public static void Init()
    {
        if (_done) return;
        _done = true;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    /// <summary>Kódování výstupu příkazového řádku Windows (OEM znaková sada aktuální kultury).</summary>
    public static Encoding Oem()
    {
        Init();
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.UTF8; }
    }

    public static Encoding Ansi()
    {
        Init();
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
        catch { return Encoding.Latin1; }
    }
}
