using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LanEmulator.Core;

public static class Goldberg
{
    public static string? AutoDetectAppId(string gameDir, string gameExePath)
    {
        // 1. steam_appid.txt
        foreach (string path in new[] {
            Path.Combine(gameDir, "steam_appid.txt"),
            Path.Combine(gameDir, "steam_settings", "steam_appid.txt") })
        {
            if (File.Exists(path))
            {
                string? n = ExtractNumber(File.ReadAllText(path).Trim());
                if (n != null) return n;
            }
        }

        // 2. DLL scan
        string? dll = File.Exists(Path.Combine(gameDir, "steam_api64.dll"))
            ? Path.Combine(gameDir, "steam_api64.dll")
            : File.Exists(Path.Combine(gameDir, "steam_api64.dll.bak"))
                ? Path.Combine(gameDir, "steam_api64.dll.bak") : null;
        if (dll != null)
        {
            try
            {
                string? found = ScanDllForAppId(File.ReadAllBytes(dll));
                if (found != null) return found;
            }
            catch { }
        }

        // 3. INI files
        foreach (string ini in Directory.GetFiles(gameDir, "*.ini"))
        {
            try
            {
                foreach (string line in File.ReadAllLines(ini))
                    if (line.StartsWith("SteamAppId=", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("AppId=", StringComparison.OrdinalIgnoreCase))
                    {
                        string? n = ExtractNumber(line.Split('=')[1]);
                        if (n != null) return n;
                    }
            }
            catch { }
        }

        return null;
    }

    public static async Task<string?> SteamSearchAppIdAsync(string gameName)
    {
        try
        {
            using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            h.DefaultRequestHeaders.Add("User-Agent", "LanEmulator/1.2.0");
            var r = await h.GetStringAsync(
                $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(gameName)}");
            using var j = System.Text.Json.JsonDocument.Parse(r);
            if (j.RootElement.GetArrayLength() == 0) return null;
            var items = j.RootElement.EnumerateArray().ToArray();

            foreach (var it in items)
            {
                string? nm = it.GetProperty("name").GetString();
                int aid = it.GetProperty("appid").GetInt32();
                if (string.Equals(nm, gameName, StringComparison.OrdinalIgnoreCase))
                    return aid.ToString();
            }

            return items.Length > 0 ? items[0].GetProperty("appid").GetInt32().ToString() : null;
        }
        catch { return null; }
    }

    private static string? ExtractNumber(string t)
    {
        var m = Regex.Match(t, @"\d+");
        return m.Success && m.Value != "0" ? m.Value : null;
    }

    private static string? ScanDllForAppId(byte[] dll)
    {
        string t = Encoding.ASCII.GetString(dll);
        int i = t.IndexOf("steam_appid", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string nb = t[Math.Max(0, i - 32)..Math.Min(t.Length, i + 128)];
        var m = Regex.Match(nb, @"(\d{2,7})");
        if (!m.Success) return null;
        int v = int.Parse(m.Value);
        return v >= 10 && v <= 9999999 ? m.Value : null;
    }
}
