namespace LanEmulator.Core.Services;

using LanEmulator.Core.Interfaces;
using LanEmulator.Core;

/// <summary>
/// Thread-safe peer registry backed by a lock-protected list.
/// </summary>
public class PeerRegistry : IPeerRegistry
{
    private readonly List<PlayerInfo> _peers = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, IPEndPoint> _ipToPeer = new();

    public IReadOnlyList<PlayerInfo> Peers
    {
        get { lock (_lock) return _peers.ToList(); }
    }

    public int Count
    {
        get { lock (_lock) return _peers.Count; }
    }

    public event PeerHandler? PeerAdded;
    public event PeerHandler? PeerRemoved;

    public void AddOrUpdate(List<PlayerInfo> players, string myId)
    {
        List<PlayerInfo> toAdd;
        lock (_lock)
        {
            _peers.Clear();
            _ipToPeer.Clear();
            _peers.AddRange(players);
            toAdd = new List<PlayerInfo>(players);
            foreach (var p in _peers)
                _ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
        }
        foreach (var p in toAdd)
            PeerAdded?.Invoke(p);
    }

    public void Clear()
    {
        List<PlayerInfo> old;
        lock (_lock)
        {
            old = new List<PlayerInfo>(_peers);
            _peers.Clear();
            _ipToPeer.Clear();
        }
        foreach (var p in old)
            PeerRemoved?.Invoke(p);
    }

    public Dictionary<string, IPEndPoint> GetIpToPeerSnapshot()
    {
        lock (_lock) return new Dictionary<string, IPEndPoint>(_ipToPeer);
    }

    public HashSet<string> GetKnownIds()
    {
        lock (_lock) return new HashSet<string>(_peers.Select(p => p.player_id));
    }

    public PlayerInfo? AddExternalPeer(PlayerInfo p)
    {
        lock (_lock)
        {
            if (_peers.Any(x => x.player_id == p.player_id))
                return null;
            _peers.Add(p);
            _ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
        }
        PeerAdded?.Invoke(p);
        return p;
    }

    public (List<string> routesToDelete, List<PlayerInfo> leftPeers) RemoveStalePeers(HashSet<string> currentIds)
    {
        var routesToDelete = new List<string>();
        var leftPeers = new List<PlayerInfo>();
        lock (_lock)
        {
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                if (!currentIds.Contains(_peers[i].player_id))
                {
                    leftPeers.Add(_peers[i]);
                    _ipToPeer.Remove(_peers[i].virtual_ip);
                    routesToDelete.Add(_peers[i].virtual_ip);
                    _peers.RemoveAt(i);
                }
            }
        }
        foreach (var p in leftPeers)
            PeerRemoved?.Invoke(p);
        return (routesToDelete, leftPeers);
    }
}
