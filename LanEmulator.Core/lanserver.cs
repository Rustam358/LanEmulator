using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanEmulator.Core;

/// <summary>
/// Built-in HTTP signaling server using HttpListener.
/// No external dependencies. No Python. No ASP.NET Core.
/// </summary>
public sealed class LanServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private CancellationTokenSource? _cts;

    // Player
    private sealed class Player
    {
        public string player_id, virtual_ip;
        public string ip;
        public int udp_port;
        public DateTime last_seen = DateTime.UtcNow;
        public Player(string pid, string ip_, int port, string vip)
        { player_id = pid; ip = ip_; udp_port = port; virtual_ip = vip; }
    }

    private sealed class Room
    {
        public readonly ConcurrentDictionary<string, Player> players = new();
    }

    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, List<ChatMsg>> _chat = new();
    private int _chatIdSeq;

    public LanServer(int port = 8000)
    {
        _port = port;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        try { _listener.Start(); }
        catch (HttpListenerException ex)
        {
            throw new Exception(
                $"Cannot start server on port {_port}.\n" +
                $"Error {ex.ErrorCode}: {ex.Message}\n\n" +
                $"Try: netsh http add urlacl url=http://+:{_port}/ user=Everyone",
                ex);
        }
        _ = ListenLoopAsync(_cts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(ctx);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
            catch { }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            resp.AddHeader("Access-Control-Allow-Origin", "*");

            string path = req.Url!.AbsolutePath;
            string method = req.HttpMethod!;

            if (method == "POST" && path == "/register")
                await HandleRegister(req, resp);
            else if (method == "GET" && path == "/poll")
                HandlePoll(req, resp);
            else if (method == "POST" && path == "/chat")
                await HandleChatPost(req, resp);
            else if (method == "GET" && path == "/chat/poll")
                HandleChatPoll(req, resp);
            else
                WriteJson(resp, new { server = "LanEmulator", version = Engine.Version });
        }
        catch (Exception ex) { Debug.WriteLine($"HandleRequest error: {ex.Message}"); }
    }

    private async Task HandleRegister(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var body = await ReadJsonAsync<RegisterBody>(req);
        if (body == null || string.IsNullOrWhiteSpace(body.player_id) || string.IsNullOrWhiteSpace(body.room_id))
        { WriteJson(resp, new { error = "player_id and room_id required" }, 400); return; }

        var room = _rooms.GetOrAdd(body.room_id, _ => new Room());
        var ip = req.RemoteEndPoint?.Address.ToString() ?? "127.0.0.1";

        // X-Forwarded-For
        string? xff = req.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(xff))
            ip = xff.Split(',')[0].Trim();

        int udpPort = body.udp_port > 0 ? body.udp_port : 51820;

        room.players.AddOrUpdate(body.player_id,
            _ => new Player(body.player_id, ip, udpPort, AssignIpForRoom(room)),
            (_, existing) =>
            {
                existing.ip = ip;
                existing.udp_port = udpPort;
                existing.last_seen = DateTime.UtcNow;
                return existing;
            });

        var player = room.players[body.player_id];
        player.last_seen = DateTime.UtcNow;
        WriteJson(resp, new { status = "ok", virtual_ip = player.virtual_ip });
    }

    private void HandlePoll(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string? roomId = req.QueryString["room_id"];
        if (string.IsNullOrEmpty(roomId) || !_rooms.TryGetValue(roomId, out var room))
        { WriteJson(resp, new { status = "waiting" }); return; }

        var now = DateTime.UtcNow;
        foreach (var kv in room.players)
            if ((now - kv.Value.last_seen).TotalSeconds > 60)
                room.players.TryRemove(kv.Key, out _);

        var active = room.players.Values.ToList();
        if (active.Count >= 2)
        {
            WriteJson(resp, new
            {
                status = "ready",
                players = active.Select(p => new
                {
                    player_id = p.player_id, ip = p.ip,
                    udp_port = p.udp_port, virtual_ip = p.virtual_ip
                }).ToList()
            });
        }
        else
            WriteJson(resp, new { status = "waiting" });
    }

    private async Task HandleChatPost(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var body = await ReadJsonAsync<ChatBody>(req);
        if (body == null || string.IsNullOrWhiteSpace(body.room_id) || string.IsNullOrWhiteSpace(body.text))
        { WriteJson(resp, new { error = "room_id and text required" }, 400); return; }

        var msgs = _chat.GetOrAdd(body.room_id, _ => new List<ChatMsg>());
        int id = Interlocked.Increment(ref _chatIdSeq);
        var msg = new ChatMsg(id, body.player_id ?? "?", body.text, DateTime.Now.ToString("HH:mm"));
        lock (msgs) { msgs.Add(msg); }
        WriteJson(resp, new { status = "ok", id });
    }

    private void HandleChatPoll(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string? roomId = req.QueryString["room_id"];
        int lastId = int.TryParse(req.QueryString["last_id"], out var id) ? id : 0;
        if (string.IsNullOrEmpty(roomId) || !_chat.TryGetValue(roomId, out var msgs))
        { WriteJson(resp, Array.Empty<object>()); return; }
        lock (msgs)
            WriteJson(resp, msgs.Where(m => m.id > lastId)
                .Select(m => new { m.id, m.player_id, m.text, m.timestamp }).ToList());
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        try { ((IDisposable)_listener).Dispose(); } catch { }
    }

    // Helpers
    private string AssignIpForRoom(Room room)
    {
        int used = room.players.Count;
        return $"10.13.37.{Math.Min(used + 1, 254)}";
    }

    private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static void WriteJson(HttpListenerResponse resp, object data, int statusCode = 200)
    {
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json";
        string json = JsonSerializer.Serialize(data, _jsonOpts);
        byte[] buf = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = buf.Length;
        resp.OutputStream.Write(buf, 0, buf.Length);
        resp.OutputStream.Close();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        string json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, _jsonOpts);
    }

    private record RegisterBody(string player_id, string room_id, int udp_port);
    private record ChatBody(string room_id, string? player_id, string text);
}

internal record ChatMsg(int id, string player_id, string text, string timestamp);
