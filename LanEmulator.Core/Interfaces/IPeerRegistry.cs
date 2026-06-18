namespace LanEmulator.Core.Interfaces;

/// <summary>
/// Thread-safe registry of connected peers with IP-to-endpoint mapping.
/// </summary>
public interface IPeerRegistry
{
    IReadOnlyList<PlayerInfo> Peers { get; }
    int Count { get; }

    event PeerHandler? PeerAdded;
    event PeerHandler? PeerRemoved;

    /// <summary>Replace the entire peer list from a poll response.</summary>
    void AddOrUpdate(List<PlayerInfo> players, string myId);

    /// <summary>Clear all peers.</summary>
    void Clear();

    /// <summary>Snapshot of IpToPeer mapping for packet routing.</summary>
    Dictionary<string, IPEndPoint> GetIpToPeerSnapshot();

    // KeepAlive-friendly atomic operations

    /// <summary>Get known player IDs for diffing.</summary>
    HashSet<string> GetKnownIds();

    /// <summary>
    /// Atomically add new peer (if not already known) and set up route.
    /// Returns the added peer or null if already known.
    /// </summary>
    PlayerInfo? AddExternalPeer(PlayerInfo p);

    /// <summary>
    /// Atomically remove stale peers not in currentIds.
    /// Returns (routes-to-delete, peers-that-left).
    /// </summary>
    (List<string> routesToDelete, List<PlayerInfo> leftPeers) RemoveStalePeers(HashSet<string> currentIds);
}
