namespace LanEmulator.Core.Interfaces;

/// <summary>
/// Signaling server abstraction — host starts it, clients talk to it.
/// Covers HTTP register/poll/chat + LAN discovery.
/// </summary>
public interface ISignalingServer
{
    /// <summary>Start built-in HTTP server (host only, no-op for clients).</summary>
    void Start(int port);

    /// <summary>Stop the built-in server.</summary>
    Task StopAsync();

    /// <summary>HTTP client primed with BaseAddress once server URL is known.</summary>
    HttpClient? HttpClient { get; set; }

    /// <summary>Register with the room server, returns assigned virtual IP.</summary>
    Task<string> RegisterAsync(string playerId, string roomId, int udpPort);

    /// <summary>Poll for connected peers. Returns all players except self.</summary>
    Task<List<PlayerInfo>?> PollAsync(string roomId, string myId);

    /// <summary>Send a chat message. Returns server-assigned message id.</summary>
    Task<int> SendChatAsync(string roomId, string playerId, string text);

    /// <summary>Poll for new chat messages since lastId.</summary>
    Task<List<ChatMessage>> PollChatAsync(int lastId, string roomId);

    /// <summary>Notify server this player is leaving.</summary>
    Task LeaveAsync(string roomId, string playerId);

    /// <summary>Firewall rules for the communication ports.</summary>
    void AddFirewallRules(int tcpPort, int udpPort);

    /// <summary>Remove firewall rules.</summary>
    void RemoveFirewallRules();

    /// <summary>Discover a LanEmulator server on the LAN via UDP broadcast.</summary>
    string? Discover(string myIp, int discoveryPort);
}
