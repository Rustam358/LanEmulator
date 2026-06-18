namespace LanEmulator.Core;

/// <summary>
/// Interface for the LAN Emulator engine.
/// Extracted to support dependency injection and unit testing.
/// </summary>
public interface IEngine
{
    // ── Events ───────────────────────────────────────────────────────
    event LogHandler OnLog;
    event StateHandler OnStateChanged;
    event PeerHandler OnPeerJoined;
    event PeerHandler OnPeerLeft;
    event Action<string> OnRoomCreated;

    // ── Properties ───────────────────────────────────────────────────
    string RoomId { get; }
    string ServerUrl { get; }
    string MyVirtualIP { get; }
    bool IsHost { get; }
    int PeerCount { get; }
    bool IsRunning { get; }
    string? GamePath { get; }

    // ── Lifecycle ────────────────────────────────────────────────────
    Task HostSetupAsync();
    string JoinSetup(string? cliUrl = null);
    void Configure(int mode, string roomId, string? gamePath = null);
    Task ConnectAsync(string serverUrl);
    Task StartVpnAsync();
    Task ShutdownAsync();

    // ── Goldberg ─────────────────────────────────────────────────────
    Task RunGoldbergAsync();

    // ── Game ─────────────────────────────────────────────────────────
    void SetGamePath(string path);
    void LaunchGame();

    // ── Chat ─────────────────────────────────────────────────────────
    Task<List<ChatMessage>> PollChatAsync(int lastId);
    Task<int> SendChatAsync(string text);

    // ── Utilities ────────────────────────────────────────────────────
    static abstract bool IsAdministrator();
    static abstract string GenerateRoomId();
    static abstract string GetLocalIP();
}
