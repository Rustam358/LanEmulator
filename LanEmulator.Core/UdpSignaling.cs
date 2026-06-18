namespace LanEmulator.Core;

/// <summary>
/// UDP-based signaling for remote peer discovery and NAT traversal.
/// Replaces the HTTP server for internet connections (LAN keeps HTTP).
/// 
/// Protocol: magic bytes "LE\x01" followed by 1-byte message type then JSON payload.
/// Messages: join_req, join_ack, poll, poll_resp, peer_update
/// </summary>
public static class UdpSignaling
{
    public const byte Magic0 = 0x4C; // 'L'
    public const byte Magic1 = 0x45; // 'E'
    public const byte Magic2 = 0x01; // version

    public const byte TypeJoinReq = 0x01;
    public const byte TypeJoinAck = 0x02;
    public const byte TypePoll = 0x03;
    public const byte TypePollResp = 0x04;

    public static readonly System.Text.Json.JsonSerializerOptions JsonOpts =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public record JoinRequest(string player_id, string room_id, string stun_ip, int stun_port);
    public record JoinAck(string player_id, string virtual_ip, int host_udp_port);
    public record PollRequest(string player_id, string room_id);
    public record PeerInfo(string player_id, string virtual_ip, string stun_ip, int stun_port);
    public record PollResponse(string status, List<PeerInfo>? peers);

    /// <summary>Build a signaling packet.</summary>
    public static byte[] Build(byte msgType, object payload)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] packet = new byte[4 + jsonBytes.Length];
        packet[0] = Magic0;
        packet[1] = Magic1;
        packet[2] = Magic2;
        packet[3] = msgType;
        Array.Copy(jsonBytes, 0, packet, 4, jsonBytes.Length);
        return packet;
    }

    /// <summary>Try to parse a signaling packet. Returns (msgType, jsonPayload) or null.</summary>
    public static (byte type, string json)? TryParse(byte[] data, int length)
    {
        if (length < 5) return null;
        if (data[0] != Magic0 || data[1] != Magic1 || data[2] != Magic2) return null;
        byte type = data[3];
        string json = System.Text.Encoding.UTF8.GetString(data, 4, length - 4);
        return (type, json);
    }

    /// <summary>Parse a join request from JSON.</summary>
    public static JoinRequest? ParseJoinRequest(string json)
        => System.Text.Json.JsonSerializer.Deserialize<JoinRequest>(json, JsonOpts);

    public static PollRequest? ParsePollRequest(string json)
        => System.Text.Json.JsonSerializer.Deserialize<PollRequest>(json, JsonOpts);
}
