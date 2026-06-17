namespace LanEmulator.Core;

public static class Helpers
{
    /// <summary>Find a working Python interpreter. Returns path or null.</summary>
    public static string? FindPython()
    {
        // Collect all candidate paths
        var candidates = new List<string>();

        // 1. Known install directories (most reliable — bypass 'where.exe' which finds Store stubs)
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pyBase = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(pyBase))
            {
                foreach (var dir in Directory.GetDirectories(pyBase))
                {
                    string exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe)) candidates.Add(exe);
                }
                // Sort descending — prefer newer Python
                candidates.Sort((a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { } // Directory read may fail on restricted systems

        // 2. 'py' launcher (C:\Windows\py.exe — part of official Python install)
        const string pyLauncher = @"C:\Windows\py.exe";
        if (File.Exists(pyLauncher)) candidates.Add(pyLauncher);

        // 3. 'where.exe' fallback (filter out WindowsApps stubs)
        foreach (string name in new[] { "python", "python3" })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("where.exe", name)
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
                p.WaitForExit(3000);
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (!string.IsNullOrEmpty(line) && !line.Contains("WindowsApps") && File.Exists(line))
                        candidates.Add(line);
                }
            }
            catch { }
        }

        // Validate: try running "path -c print(1)" — exit code 0 = working Python
        foreach (string exe in candidates)
        {
            try
            {
                var test = Process.Start(new ProcessStartInfo(exe, "-c \"print(1)\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
                test.WaitForExit(5000);
                if (test.ExitCode == 0) return exe;
            }
            catch { }
        }
        return null;
    }

    public static string Win32Msg(int c) => new Win32Exception(c).Message;

    public static void RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        p.WaitForExit(10_000);
    }

    public static void RunRoute(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("route", args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        p.WaitForExit(5000);
    }

    public static void RunRouteSilent(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("route", args)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    public static void RunSilent(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    public static string GetInterfaceAlias(ulong luid)
    {
        char[] b = new char[512];
        if (WintunInterop.ConvertInterfaceLuidToAlias(ref luid, b, (nuint)(b.Length * 2)) != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int e = Array.IndexOf(b, '\0');
        return new string(b, 0, e >= 0 ? e : b.Length);
    }

    public static string? DiscoverServer()
    {
        try
        {
            using var sock = new UdpClient();
            sock.EnableBroadcast = true;
            sock.Client.ReceiveTimeout = 3000;
            byte[] ping = "LANEMULATOR_DISCOVER"u8.ToArray();
            sock.Send(ping, ping.Length, new IPEndPoint(IPAddress.Broadcast, 51821));
            var remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] response = sock.Receive(ref remote);
            return Encoding.UTF8.GetString(response);
        }
        catch { return null; }
    }

    /// <summary>Auto-install Wintun driver via WireGuard MSI. Returns false if reboot needed.</summary>
    public static async Task<bool> AutoInstallDriverAsync(Action<string>? onProgress = null)
    {
        string msiUrl = "https://download.wireguard.com/windows-client/wireguard-amd64-1.1.msi";
        string msiPath = Path.Combine(Path.GetTempPath(), "wintun-driver.msi");

        try
        {
            onProgress?.Invoke("Downloading driver…");
            using (var hc = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                var bytes = await hc.GetByteArrayAsync(msiUrl);
                await File.WriteAllBytesAsync(msiPath, bytes);
            }

            onProgress?.Invoke("Installing…");
            var psi = new ProcessStartInfo("msiexec", $"/i \"{msiPath}\" /quiet /norestart")
            { UseShellExecute = false, CreateNoWindow = true };
            var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();

            try { File.Delete(msiPath); } catch { }

            // Disable WireGuard service
            RunSilent("sc", "stop WireGuardTunnel$LanEmulatorTun");
            RunSilent("sc", "stop WireGuardManager");
            RunSilent("sc", "config WireGuardManager start=disabled");
            foreach (var wg in Process.GetProcessesByName("wireguard"))
                try { wg.Kill(); } catch { }

            // Start wintun kernel driver
            RunSilent("sc", "start wintun");

            // Wait for driver
            onProgress?.Invoke("Waiting for driver…");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                if (WintunInterop.WintunGetRunningDriverVersion() != 0)
                    return true;
            }
            return false; // driver not loaded, may need reboot
        }
        catch { return false; }
    }
}
