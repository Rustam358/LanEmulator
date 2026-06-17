using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanEmulator.Core;

public delegate void PeerEvent(PlayerInfo peer);

public static class KeepAlive
{
    public static async Task RunAsync(HttpClient http, string roomId, int udpPort,
        List<PlayerInfo> peers, Dictionary<string, IPEndPoint> ipToPeer,
        object peerLock, CancellationToken ct,
        PeerEvent onJoin, PeerEvent onLeft)
    {
        string myId = Environment.MachineName;
        var knownIds = new HashSet<string>();
        lock (peerLock) { foreach (var p in peers) knownIds.Add(p.player_id); }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10_000, ct);
                var regReq = new { player_id = myId, room_id = roomId, udp_port = udpPort };
                await http.PostAsJsonAsync("/register", regReq, ct);
                var poll = await http.GetFromJsonAsync<PollResponse>($"/poll?room_id={Uri.EscapeDataString(roomId)}", ct);

                if (poll is not { status: "ready", players: not null }) continue;
                var current = poll.players.FindAll(p =>
                    !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));
                var currentIds = new HashSet<string>(current.Select(p => p.player_id));

                var routesToDelete = new List<string>();
                var leftPeers = new List<PlayerInfo>();

                lock (peerLock)
                {
                    for (int i = peers.Count - 1; i >= 0; i--)
                    {
                        if (!currentIds.Contains(peers[i].player_id))
                        {
                            leftPeers.Add(peers[i]);
                            ipToPeer.Remove(peers[i].virtual_ip);
                            routesToDelete.Add(peers[i].virtual_ip);
                            peers.RemoveAt(i);
                        }
                    }

                    foreach (var p in current)
                    {
                        if (!knownIds.Contains(p.player_id))
                        {
                            peers.Add(p);
                            ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                            onJoin(p);
                        }
                    }
                }

                foreach (var ip in routesToDelete)
                    Helpers.RunRouteSilent($"delete {ip}");
                foreach (var p in leftPeers)
                    onLeft(p);

                knownIds = currentIds;
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }
}
