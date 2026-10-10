using BudisCommander.Core;
using FluentFTP;

namespace BudisCommander.Remote;

public sealed class FtpSession : RemoteSession
{
    private readonly AsyncFtpClient _client;
    private readonly string _user;
    private string _startPath = "/";

    public FtpSession(string host, int port, string user, string password, bool tls, bool insecure)
    {
        Host = host;
        _user = user;
        _client = new AsyncFtpClient(host, string.IsNullOrEmpty(user) ? "anonymous" : user,
                                     string.IsNullOrEmpty(user) ? "anonymous@" : password, port);
        _client.Config.ConnectTimeout = 15000;
        _client.Config.DataConnectionConnectTimeout = 15000;
        if (tls)
        {
            _client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            _client.Config.ValidateAnyCertificate = insecure;
        }
    }

    public override string DisplayName => "ftp://" + (_user.Length > 0 ? _user + "@" : "") + Host;
    public override string StartPath => _startPath;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await _client.Connect(ct);
        try { _startPath = RemotePath.Normalize(await _client.GetWorkingDirectory(ct)); } catch { _startPath = "/"; }
    }

    public override async Task<List<FileEntry>> ListAsync(string path, CancellationToken ct)
    {
        var items = await _client.GetListing(path, ct);
        var result = new List<FileEntry>();
        foreach (var i in items)
        {
            if (i.Name is "." or "..") continue;
            bool isDir = i.Type == FtpObjectType.Directory || i.Type == FtpObjectType.Link && i.LinkObject?.Type == FtpObjectType.Directory;
            result.Add(new FileEntry
            {
                Name = i.Name,
                FullPath = RemotePath.Child(path, i.Name),
                IsDirectory = isDir,
                Size = i.Size < 0 ? 0 : i.Size,
                Modified = i.Modified == DateTime.MinValue ? null : i.Modified,
                IsRemote = true,
            });
        }
        return result;
    }

    protected override async Task DownloadFileAsync(string remote, string local, long size, Action<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        IProgress<FtpProgress>? p = progress == null ? null : new Progress<FtpProgress>(x => progress(Math.Clamp(x.Progress / 100.0, 0, 1)));
        var status = await _client.DownloadFile(local, remote, FtpLocalExists.Overwrite, FtpVerify.None, p, ct);
        if (status == FtpStatus.Failed) throw new IOException("Stažení souboru selhalo: " + remote);
    }

    protected override async Task UploadFileAsync(string local, string remote, Action<double>? progress, CancellationToken ct)
    {
        IProgress<FtpProgress>? p = progress == null ? null : new Progress<FtpProgress>(x => progress(Math.Clamp(x.Progress / 100.0, 0, 1)));
        var status = await _client.UploadFile(local, remote, FtpRemoteExists.Overwrite, true, FtpVerify.None, p, ct);
        if (status == FtpStatus.Failed) throw new IOException("Nahrání souboru selhalo: " + local);
    }

    protected override Task DeleteFileAsync(string path, CancellationToken ct) => _client.DeleteFile(path, ct);
    protected override Task DeleteEmptyDirectoryAsync(string path, CancellationToken ct) => _client.DeleteDirectory(path, ct);
    public override Task MkdirAsync(string path, CancellationToken ct) => _client.CreateDirectory(path, ct);

    public override async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        await _client.Rename(from, to, ct);
    }

    public override async Task<byte[]> ReadHeadAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var stream = await _client.OpenRead(path, FtpDataType.Binary, 0, false, ct);
        var buf = new byte[maxBytes];
        int total = 0;
        while (total < maxBytes)
        {
            int n = await stream.ReadAsync(buf.AsMemory(total, maxBytes - total), ct);
            if (n <= 0) break;
            total += n;
        }
        return buf.AsSpan(0, total).ToArray();
    }

    public override void Dispose() => _client.Dispose();
}
