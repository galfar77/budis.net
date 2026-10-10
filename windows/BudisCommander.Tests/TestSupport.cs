using BudisCommander.Core;

namespace BudisCommander.Tests;

public sealed class FakeUi : IUserInterface
{
    public List<string> Errors { get; } = new();
    public bool Confirm { get; set; } = true;
    public string? PromptAnswer { get; set; }
    public string? PasswordAnswer { get; set; }
    public int Choice { get; set; }
    public ConflictChoice Conflict { get; set; } = ConflictChoice.Overwrite;
    public Task ShowErrorAsync(string message) { Errors.Add(message); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string info, string okText) => Task.FromResult(Confirm);
    public Task<string?> PromptAsync(string title, string info, string initial, string okText) => Task.FromResult(PromptAnswer ?? initial);
    public Task<string?> PromptPasswordAsync(string title, string info) => Task.FromResult(PasswordAnswer);
    public Task<int> ChooseAsync(string title, string info, string[] buttons) => Task.FromResult(Choice);
    public Task<ConflictChoice> AskConflictAsync(string name) => Task.FromResult(Conflict);
    public bool TrustHostKey(string host, string fingerprint) => true;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "budis-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() { Directory.CreateDirectory(Path); }
    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    public string Dir(string name) { var d = Combine(name); Directory.CreateDirectory(d); return d; }
    public string File(string name, string content = "x")
    {
        var f = Combine(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
        System.IO.File.WriteAllText(f, content);
        return f;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public static class Helpers
{
    public static AppCore NewCore(string left, string right, FakeUi? ui = null)
    {
        var settings = new AppSettings();
        settings.Session = new SessionState { Left = new() { left }, Right = new() { right }, ActiveLeft = true };
        var core = new AppCore(settings, ui ?? new FakeUi(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "budis-settings-" + Guid.NewGuid().ToString("N") + ".json"));
        core.LoadAllAsync().GetAwaiter().GetResult();
        return core;
    }
}
