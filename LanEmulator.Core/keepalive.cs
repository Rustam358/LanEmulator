namespace LanEmulator.Core;

public delegate void PeerEvent(PlayerInfo peer);

public static class KeepAlive
{
    public static async Task RunAsync(
        HttpClient http, string roomId, int udpPort,
        Interfaces.IPeerRegistry peerRegistry,
        CancellationToken ct,
        PeerEvent onJoin, PeerEvent onLeft)
    {
        string myId = Environment.MachineName;
        var knownIds = peerRegistry.GetKnownIds();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10_000, ct);
                var regReq = new { player_id = myId, room_id = roomId, udp_port = udpPort };
                await http.PostAsJsonAsync("/register", regReq, ct);
                var poll = await http.GetFromJsonAsync<PollResponse>(
                    $"/poll?room_id={Uri.EscapeDataString(roomId)}", ct);

                if (poll is not { status: "ready", players: not null }) continue;
                var current = poll.players.FindAll(p =>
                    !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));
                var currentIds = new HashSet<string>(current.Select(p => p.player_id));

                // Add new peers
                foreach (var p in current)
                {
                    if (!knownIds.Contains(p.player_id))
                    {
                        var added = peerRegistry.AddExternalPeer(p);
                        if (added != null) onJoin(added);
                    }
                }

                // Remove stale peers
                var (routesToDelete, leftPeers) = peerRegistry.RemoveStalePeers(currentIds);
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
