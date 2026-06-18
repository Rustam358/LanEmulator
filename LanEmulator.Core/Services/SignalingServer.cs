namespace LanEmulator.Core.Services;

using LanEmulator.Core.Interfaces;
using LanEmulator.Core;

/// <summary>
/// Signaling server implementation — wraps LanServer + HttpClient + firewall.
/// </summary>
public class SignalingServer : ISignalingServer, IDisposable
{
    private LanServer? _server;
    private HttpClient? _http;

    public HttpClient? HttpClient
    {
        get => _http;
        set => _http = value;
    }

    public void Start(int port)
    {
        _server = new LanServer(port);
        _server.Start();
    }

    public async Task StopAsync()
    {
        if (_server != null)
        {
            await _server.StopAsync();
            _server.Dispose();
            _server = null;
        }
    }

    public async Task<string> RegisterAsync(string playerId, string roomId, int udpPort)
    {
        if (_http == null) throw new InvalidOperationException("HttpClient not set");
        var regReq = new { player_id = playerId, room_id = roomId, udp_port = udpPort };
        var resp = await _http.PostAsJsonAsync("/register", regReq);
        resp.EnsureSuccessStatusCode();
        var reg = await resp.Content.ReadFromJsonAsync<RegisterResponse>();
        return reg?.virtual_ip ?? "10.13.37.1";
    }

    public async Task<List<PlayerInfo>?> PollAsync(string roomId, string myId)
    {
        if (_http == null) return null;
        var poll = await _http.GetFromJsonAsync<PollResponse>(
            $"/poll?room_id={Uri.EscapeDataString(roomId)}");
        if (poll is { status: "ready", players: not null })
        {
            return poll.players.FindAll(p =>
                !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    public async Task<int> SendChatAsync(string roomId, string playerId, string text)
    {
        if (_http == null) return -1;
        var msg = new { room_id = roomId, player_id = playerId, text };
        var resp = await _http.PostAsJsonAsync("/chat", msg);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<ChatSendResponse>();
            return result?.id ?? -1;
        }
        return -1;
    }

    public async Task<List<ChatMessage>> PollChatAsync(int lastId, string roomId)
    {
        if (_http == null) return new();
        var msgs = await _http.GetFromJsonAsync<List<ChatMessage>>(
            $"/chat/poll?room_id={Uri.EscapeDataString(roomId)}&last_id={lastId}");
        return msgs ?? new();
    }

    public async Task LeaveAsync(string roomId, string playerId)
    {
        if (_http == null) return;
        await _http.PostAsync(
            $"/leave?room_id={Uri.EscapeDataString(roomId)}&player_id={Uri.EscapeDataString(playerId)}",
            null);
    }

    public void AddFirewallRules(int tcpPort, int udpPort)
    {
        Helpers.RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator Server\" dir=in action=allow protocol=TCP localport={tcpPort}");
        Helpers.RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator UDP\" dir=in action=allow protocol=UDP localport={udpPort}");
        Helpers.RunSilent("netsh", $"interface portproxy add v4tov4 listenport={tcpPort} connectaddress=127.0.0.1 connectport={tcpPort}");
    }

    public void RemoveFirewallRules()
    {
        Helpers.RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator Server\"");
        Helpers.RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator UDP\"");
    }

    public string? Discover(string myIp, int discoveryPort)
    {
        return Helpers.DiscoverServer();
    }

    public void Dispose()
    {
        _server?.Dispose();
        _http?.Dispose();
    }
}
