using System.ComponentModel;
using System.IO;
using Avalonia.Media;

namespace BudisCommander.Core;

/// <summary>Položka v panelu (lokální soubor, soubor na serveru nebo nadřazená složka „..“).</summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public FileAttributes Attributes { get; init; }
    public bool IsRemote { get; init; }
    /// <summary>Relativní cesta v plochém zobrazení všech podsložek.</summary>
    public string? SubPath { get; init; }

    public string Id => IsParent ? ".." : FullPath;
    public string Ext => IsDirectory || IsParent ? "" : Path.GetExtension(Name).TrimStart('.');

    /// <summary>Jestli se má zobrazovat přípona zvlášť (nastavuje se z nastavení).</summary>
    public static bool SplitExtension { get; set; }
    public static IBrush NormalBrush { get; set; } = Brushes.Black;
    public static IBrush MarkBrush { get; set; } = Brushes.Red;
    public static IBrush DirBrush { get; set; } = Brushes.SteelBlue;

    public string DisplayName
    {
        get
        {
            if (SubPath != null) return SubPath;
            if (SplitExtension && !IsDirectory && !IsParent && Ext.Length > 0) return Path.GetFileNameWithoutExtension(Name);
            return Name;
        }
    }

    public string Icon => IsParent ? "↰" : IsDirectory ? "📁" : "📄";

    public string AttrText
    {
        get
        {
            if (IsParent || IsRemote) return "";
            var a = Attributes;
            return ((a & FileAttributes.ReadOnly) != 0 ? "R" : "-")
                 + ((a & FileAttributes.Hidden) != 0 ? "H" : "-")
                 + ((a & FileAttributes.System) != 0 ? "S" : "-")
                 + ((a & FileAttributes.Archive) != 0 ? "A" : "-");
        }
    }

    private bool _marked;
    public bool IsMarked
    {
        get => _marked;
        set { if (_marked == value) return; _marked = value; Raise(nameof(IsMarked)); Raise(nameof(Foreground)); }
    }

    private long? _dirSize;
    public long? DirSize
    {
        get => _dirSize;
        set { _dirSize = value; Raise(nameof(DirSize)); Raise(nameof(SizeText)); }
    }

    private bool _sizePending;
    public bool SizePending
    {
        get => _sizePending;
        set { _sizePending = value; Raise(nameof(SizeText)); }
    }

    public string SizeText
    {
        get
        {
            if (IsParent) return "";
            if (IsDirectory) return _dirSize.HasValue ? Formatting.Size(_dirSize.Value) : (_sizePending ? "…" : "‹DIR›");
            return Formatting.Size(Size);
        }
    }

    public string DateText => Formatting.Date(Modified);

    public IBrush Foreground => IsMarked ? MarkBrush : (IsDirectory ? DirBrush : NormalBrush);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void RefreshBrush() => Raise(nameof(Foreground));

    public static FileEntry Parent(string path, bool remote = false) => new()
    {
        Name = "..", FullPath = path, IsDirectory = true, IsParent = true, IsRemote = remote
    };
}
