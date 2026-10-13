using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace BudisCommander.Core;

public sealed record LanHost(string Address, string Name);
public sealed record ShareInfo(string Name, string Remark, bool IsDisk)
{
    /// <summary>Sdílená složka vhodná k otevření: diskový prostředek, ne správcovský (název končící $).</summary>
    public bool IsBrowsable => IsDisk && !Name.EndsWith('$');
}

/// <summary>Hledání počítačů v místní síti (otevřený port SMB) a výpis jejich sdílených složek.</summary>
public static class LanScanner
{
    public const int SmbPort = 445;

    /// <summary>Adresy v podsíti (bez adresy sítě, vysílací adresy a vlastní adresy); větší podsítě než /24 se zúží na /24 kolem vlastní adresy.</summary>
    public static List<string> SubnetAddresses(IPAddress own, IPAddress mask)
    {
        var ip = own.GetAddressBytes(); var m = mask.GetAddressBytes();
        if (ip.Length != 4 || m.Length != 4) return new List<string>();
        uint addr = ToUInt(ip), msk = ToUInt(m);
        if (msk < 0xFFFFFF00u) msk = 0xFFFFFF00u;                       // nejvýš 254 počítačů
        uint net = addr & msk, broadcast = net | ~msk;
        var list = new List<string>();
        for (var a = net + 1; a < broadcast; a++)
            if (a != addr) list.Add(new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }).ToString());
        return list;
    }

    private static uint ToUInt(byte[] b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    /// <summary>IPv4 adresy a masky aktivních síťových rozhraní (bez smyčky).</summary>
    public static List<(IPAddress Ip, IPAddress Mask)> LocalNetworks()
    {
        var list = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && ua.IPv4Mask != null) list.Add((ua.Address, ua.IPv4Mask));
            }
        }
        catch { /* bez sítě není co hledat */ }
        return list;
    }

    /// <summary>Zkusí se připojit na port každé adresy a vrátí ty, které odpověděly; jména se zjistí zpětným dotazem DNS.</summary>
    public static async Task<List<LanHost>> ScanAsync(IEnumerable<string> addresses, int port = SmbPort, int timeoutMs = 500, int parallel = 64,
                                                      IProgress<int>? found = null, CancellationToken ct = default)
    {
        var hosts = new List<LanHost>();
        using var gate = new SemaphoreSlim(parallel);
        var tasks = addresses.Select(async address =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (!await IsOpenAsync(address, port, timeoutMs, ct)) return;
                var name = await NameOfAsync(address);
                lock (hosts) { hosts.Add(new LanHost(address, name)); found?.Report(hosts.Count); }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* adresa neodpovídá */ }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        lock (hosts) return hosts.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(h => h.Address).ToList();
    }

    public static async Task<bool> IsOpenAsync(string address, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try { await client.ConnectAsync(address, port, timeout.Token); return client.Connected; }
        catch { return false; }
    }

    public static async Task<string> NameOfAsync(string address)
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            var entry = await Dns.GetHostEntryAsync(address, cts.Token);
            return string.IsNullOrEmpty(entry.HostName) ? address : entry.HostName.Split('.')[0];
        }
        catch { return address; }
    }

    // --- sdílené složky počítače (NetShareEnum, jen Windows) ----------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1 { public string shi1_netname; public uint shi1_type; public string shi1_remark; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int prefMaxLen, out int read, out int total, ref int resume);
    [DllImport("netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Sdílené složky počítače (typ 0 = disk). Chyba 5 znamená, že je nutné přihlášení (net use).</summary>
    public static List<ShareInfo> ListShares(string host)
    {
        var result = new List<ShareInfo>();
        if (!OperatingSystem.IsWindows()) return result;
        var resume = 0;
        var rc = NetShareEnum(@"\\" + host, 1, out var buffer, -1, out var read, out _, ref resume);
        if (rc == 5) throw new UnauthorizedAccessException("Počítač vyžaduje přihlášení (zadejte uživatele a heslo níže a zkuste to znovu).");
        if (rc != 0 && rc != 234) throw new IOException($"Sdílené složky se nepodařilo zjistit (kód {rc}).");
        try
        {
            var size = Marshal.SizeOf<SHARE_INFO_1>();
            for (var i = 0; i < read; i++)
            {
                var s = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * size);
                result.Add(new ShareInfo(s.shi1_netname, s.shi1_remark ?? "", (s.shi1_type & 0xFF) == 0));
            }
        }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
