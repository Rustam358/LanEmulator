namespace LanEmulator.Core;

// ============================================================================
// Server API models
// ============================================================================

public record RegisterResponse(string status, string room_id, string? virtual_ip, int player_count);
public record PollResponse(string status, int player_count, List<PlayerInfo>? players);
public record PlayerInfo(string player_id, string ip, int udp_port, string virtual_ip);
public record ChatMessage(int id, string player_id, string text, string timestamp);
public record ChatSendResponse(string status, int id);

// ============================================================================
// Engine events and logging
// ============================================================================

public enum LogLevel { Info, Ok, Warn, Error, PeerJoin, PeerLeft, Chat }

// ============================================================================
// Delegates
// ============================================================================

public delegate void LogHandler(LogEntry entry);
public delegate void StateHandler(string state, string? detail);
public delegate void PeerHandler(PlayerInfo peer);
public delegate void ChatHandler(string player, string text, string timestamp);

// ============================================================================
// Log entry model
// ============================================================================

public record LogEntry
{
    public LogLevel Level { get; init; }
    public string Message { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
}

