using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BudisCommander.Core;
using BudisCommander.Ui;
using Xunit;

namespace BudisCommander.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public class LanguageCollection { }

/// <summary>Testy překladu běží osamoceně, protože jazyk je společný pro celý proces.</summary>
[Collection("Language")]
public class TranslationTests
{
    [Fact]
    public void ExactPatternAndPrefixTranslations()
    {
        try
        {
            Tr.SetLanguage("en");
            Assert.Equal("Rename", Tr.T("Přejmenovat"));
            Assert.Equal("Marked 3 of 10, 1.2 MB", Tr.T("Označeno 3 z 10, 1.2 MB"));
            Assert.Equal("Copying: report.docx", Tr.T("Kopíruji: report.docx"));          // předpona
            Assert.Equal("Nejaky neznamy text", Tr.T("Nejaky neznamy text"));              // co nezná, nechá
            Assert.Equal("Name ▲", Tr.T("Název ▲"));
            Assert.Equal("Folder \"C:\\x\" does not exist.", Tr.T("Složka „C:\\x“ neexistuje."));
            Assert.Equal("Tab set \"Projekt X\" opened.", Tr.T("Sada záložek „Projekt X“ otevřena."));
            Tr.SetLanguage("cs");
            Assert.Equal("Přejmenovat", Tr.T("Přejmenovat"));
        }
        finally { Tr.SetLanguage("cs"); }
    }

    [Fact]
    public void EveryActionTitleAndMenuHasEnglish()
    {
        try
        {
            Tr.SetLanguage("en");
            var czech = new List<string>();
            foreach (var a in Actions.All)
            {
                if (Tr.T(a.Title) == a.Title) czech.Add(a.Title);
                if (a.Menu != "" && Tr.T(a.Menu) == a.Menu) czech.Add(a.Menu);
            }
            Assert.Empty(czech);
        }
        finally { Tr.SetLanguage("cs"); }
    }

    [Fact]
    public void EveryDictionaryLineHasTabAndMatchingPlaceholders()
    {
        foreach (var line in EnglishStringsAccessor.Lines())
        {
            var parts = line.Split('\t');
            Assert.True(parts.Length == 2, "Špatný řádek: " + line);
            int Count(string s) => s.Split("{}").Length - 1;
            // v angličtině mohou být očíslované zástupky {1}; jinak musí být stejný počet
            if (!System.Text.RegularExpressions.Regex.IsMatch(parts[1], @"\{\d+\}"))
                Assert.True(Count(parts[0]) == Count(parts[1]), "Počet {} se liší: " + line);
        }
    }

    [AvaloniaFact]
    public async Task MainWindowIsEnglish()
    {
        using var t = new TempDir();
        t.File("l/alpha.txt", "A"); t.Dir("l/Docs"); t.File("r/gamma.zip", "G");
        try
        {
            var settings = new AppSettings { Language = "en" };
            settings.Session = new SessionState { Left = new() { t.Combine("l") }, Right = new() { t.Combine("r") }, ActiveLeft = true };
            var w = new MainWindow(settings, t.Combine("settings.json"), new FakeUi());
            w.Show();
            for (int i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
            var menu = w.GetVisualDescendants().OfType<Menu>().First();
            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
            Assert.Equal(new[] { "File", "Mark", "Tools", "View", "Servers" }, headers);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
            Assert.Contains("Rename", texts);
            Assert.Contains("New folder", texts);
            var dir = Environment.GetEnvironmentVariable("BUDIS_SHOTS");
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); w.CaptureRenderedFrame()?.Save(System.IO.Path.Combine(dir, "main-en.png")); }
            w.Close();
        }
        finally { Tr.SetLanguage("cs"); }
    }
}
