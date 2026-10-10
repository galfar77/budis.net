using BudisCommander.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace BudisCommander.Remote;

public sealed class SftpSession : RemoteSession
{
    private readonly SftpClient _client;
    private readonly string _user;
    private readonly Func<string, string, bool> _trustHost;
    private string _startPath = "/";

    /// <param name="trustHost">Rozhodne o důvěře klíči serveru (host, otisk SHA-256 v base64).</param>
    public SftpSession(string host, int port, string user, string password, string keyPath,
                       Func<string, string, bool> trustHost)
    {
        Host = host;
        _user = user;
        _trustHost = trustHost;

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            var key = string.IsNullOrEmpty(password) ? new PrivateKeyFile(keyPath) : new PrivateKeyFile(keyPath, password);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = password;
            };
            methods.Add(ki);
        }
        var info = new ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
        _client = new SftpClient(info);
        _client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = _trustHost(Host, e.HostKeyName + ":" + e.FingerPrintSHA256);
        };
    }

    public override string DisplayName => "sftp://" + (_user.Length > 0 ? _user + "@" : "") + Host;
    public override string StartPath => _startPath;

    public override Task ConnectAsync(CancellationToken ct) => Task.Run(() =>
    {
        _client.Connect();
        _startPath = RemotePath.Normalize(_client.WorkingDirectory);
    }, ct);

    public override Task<List<FileEntry>> ListAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        var result = new List<FileEntry>();
        foreach (var f in _client.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            bool isDir = f.IsDirectory || f.IsSymbolicLink && SafeIsDir(f.FullName);
            result.Add(new FileEntry
            {
                Name = f.Name,
                FullPath = RemotePath.Normalize(f.FullName),
                IsDirectory = isDir,
                Size = f.Length,
                Modified = f.LastWriteTime,
                IsRemote = true,
            });
        }
        return result;
    }, ct);

    private bool SafeIsDir(string path)
    {
        try { return _client.GetAttributes(path).IsDirectory; } catch { return false; }
    }

    // Zrušení se nesmí provádět výjimkou ve zpětném volání (spadl by celý proces); SSH.NET má pro to příznak v AsyncResult.
    protected override Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        try
        {
            using (var fs = new FileStream(local, FileMode.Create, FileAccess.Write))
            {
                var ar = (SftpDownloadAsyncResult)_client.BeginDownloadFile(remote, fs, null, null,
                    done => { if (size > 0) progress?.Invoke(Math.Clamp((double)done / size, 0, 1)); });
                using (ct.Register(() => ar.IsDownloadCanceled = true))
                {
                    try { _client.EndDownloadFile(ar); }
                    catch when (ct.IsCancellationRequested) { }
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            if (ct.IsCancellationRequested) { try { File.Delete(local); } catch { } }
            throw;
        }
    }, ct);

    protected override Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long size = Math.Max(fs.Length, 1);
        var ar = (SftpUploadAsyncResult)_client.BeginUploadFile(fs, remote, true, null, null,
            done => progress?.Invoke(Math.Clamp((double)done / size, 0, 1)));
        using (ct.Register(() => ar.IsUploadCanceled = true))
        {
            try { _client.EndUploadFile(ar); }
            catch when (ct.IsCancellationRequested) { }
        }
        if (ct.IsCancellationRequested)
        {
            try { _client.DeleteFile(remote); } catch { }
            ct.ThrowIfCancellationRequested();
        }
    }, ct);

    protected override Task DeleteFileAsync(string path, CancellationToken ct) => Task.Run(() => _client.DeleteFile(path), ct);
    protected override Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct) => Task.Run(() => _client.DeleteDirectory(path), ct);
    public override Task MkdirAsync(string path, CancellationToken ct) => Task.Run(() => _client.CreateDirectory(path), ct);
    public override Task RenameAsync(string from, string to, CancellationToken ct) => Task.Run(() => _client.RenameFile(from, to), ct);

    public override Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct) => Task.Run(() =>
    {
        using var s = _client.OpenRead(path);
        var buf = new byte[maxBytes];
        int total = 0;
        while (total < maxBytes)
        {
            int n = s.Read(buf, total, maxBytes - total);
            if (n <= 0) break;
            total += n;
        }
        return buf.AsSpan(0, total).ToArray();
    }, ct);

    public override void Dispose() => _client.Dispose();
}
