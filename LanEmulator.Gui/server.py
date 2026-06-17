"""
Wintun LAN Emulator — Signaling Server v1.0.0
FastAPI + uvicorn | UDP discovery responder on port 51821
"""
import socket
import threading
from fastapi import FastAPI, Request, Query, HTTPException
from pydantic import BaseModel
from datetime import datetime, timezone, timedelta
from ipaddress import IPv4Address

app = FastAPI(title="LanEmulator Signaling Server")

MAX_PLAYERS = 20
VPN_SUBNET = IPv4Address("10.13.37.0")
PLAYER_TIMEOUT = timedelta(seconds=60)
IP_RECYCLE_TIMEOUT = timedelta(seconds=120)

rooms: dict[str, dict] = {}

# Room chat history (max 200 messages per room)
chat_messages: dict[str, list[dict]] = {}
DISCOVERY_PORT = 51821


def get_local_ip() -> str:
    """Get the primary LAN IP address."""
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(('8.8.8.8', 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return '127.0.0.1'


def discovery_responder():
    """Background UDP responder: answers LAN discovery pings."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind(('0.0.0.0', DISCOVERY_PORT))
    except OSError:
        return
    local_ip = get_local_ip()
    print(f"[DISCOVERY] Listening on UDP {DISCOVERY_PORT}, server at http://{local_ip}:8000")
    while True:
        try:
            data, addr = sock.recvfrom(1024)
            if data == b'LANEMULATOR_DISCOVER':
                sock.sendto(f'http://{local_ip}:8000'.encode(), addr)
        except Exception:
            break


class RegisterRequest(BaseModel):
    player_id: str
    room_id: str
    udp_port: int = 51820


def _cleanup_room(room: dict, now: datetime):
    stale = [pid for pid, pinfo in room["players"].items()
             if now - datetime.fromisoformat(pinfo["last_seen"]) > PLAYER_TIMEOUT]
    for pid in stale:
        print(f"[TIMEOUT] {pid} removed (no re-register for 60s)")
        del room["players"][pid]


def _find_free_ip(room: dict) -> int:
    now = datetime.now(timezone.utc)
    used = set()
    recycled = []
    for pid, pinfo in room["players"].items():
        age = now - datetime.fromisoformat(pinfo["last_seen"])
        if age <= IP_RECYCLE_TIMEOUT:
            used.add(pinfo["ip_index"])
        else:
            recycled.append(pinfo["ip_index"])
    if recycled:
        return min(recycled)
    ip = 1
    while ip in used:
        ip += 1
    return ip


@app.post("/register")
async def register(req: RegisterRequest, request: Request):
    room_id = req.room_id.strip().lower()
    player_id = req.player_id.strip()
    if not room_id or not player_id:
        raise HTTPException(400, "player_id and room_id are required")

    now = datetime.now(timezone.utc)
    if room_id not in rooms:
        rooms[room_id] = {"players": {}, "created": now}
    room = rooms[room_id]
    _cleanup_room(room, now)

    fwd = request.headers.get("X-Forwarded-For")
    public_ip = (
        (fwd.split(",")[0].strip() if fwd else None) or
        (request.client.host if request.client else "unknown")
    )

    if player_id in room["players"]:
        existing = room["players"][player_id]
        existing.update(ip=public_ip, udp_port=req.udp_port,
                        last_seen=now.isoformat())
        return {"status": "ok", "room_id": room_id,
                "virtual_ip": str(VPN_SUBNET + existing["ip_index"]),
                "player_count": len(room["players"])}

    active = sum(1 for p in room["players"].values()
                 if now - datetime.fromisoformat(p["last_seen"]) <= PLAYER_TIMEOUT)
    if active >= MAX_PLAYERS:
        raise HTTPException(409, f"Room is full (max {MAX_PLAYERS})")

    ip_index = _find_free_ip(room)
    virtual_ip = str(VPN_SUBNET + ip_index)
    room["players"][player_id] = {
        "ip": public_ip, "udp_port": req.udp_port,
        "virtual_ip": virtual_ip, "ip_index": ip_index,
        "last_seen": now.isoformat(), "joined": now.isoformat()
    }
    print(f"[REGISTER] {player_id} → room {room_id} | {public_ip}:{req.udp_port} vpn={virtual_ip}")
    return {"status": "ok", "room_id": room_id, "virtual_ip": virtual_ip,
            "player_count": len(room["players"])}


@app.get("/poll")
async def poll(room_id: str = Query(..., description="Room ID")):
    room_id = room_id.strip().lower()
    if room_id not in rooms:
        raise HTTPException(404, "Room not found")
    room = rooms[room_id]
    now = datetime.now(timezone.utc)
    active = {pid: pinfo for pid, pinfo in room["players"].items()
              if now - datetime.fromisoformat(pinfo["last_seen"]) <= PLAYER_TIMEOUT}
    if len(active) < 2:
        return {"status": "waiting", "player_count": len(active), "players": []}
    return {"status": "ready", "player_count": len(active), "players": [
        {"player_id": pid, "ip": p["ip"], "udp_port": p["udp_port"], "virtual_ip": p["virtual_ip"]}
        for pid, p in sorted(active.items(), key=lambda x: x[1]["joined"])
    ]}


@app.post("/leave")
async def leave(room_id: str = Query(...), player_id: str = Query(...)):
    room_id = room_id.strip().lower()
    player_id = player_id.strip()
    if room_id not in rooms or player_id not in rooms[room_id]["players"]:
        return {"status": "ok"}
    vpn = rooms[room_id]["players"][player_id]["virtual_ip"]
    del rooms[room_id]["players"][player_id]
    print(f"[LEAVE] {player_id} left room {room_id} (was {vpn})")
    return {"status": "ok"}


@app.get("/rooms")
async def list_rooms():
    return {rid: {"player_count": len(i["players"]),
                  "players": {p: v["virtual_ip"] for p, v in i["players"].items()},
                  "created": i["created"].isoformat()} for rid, i in rooms.items()}


@app.get("/health")
async def health():
    return {"status": "ok", "rooms": len(rooms)}



# ═══ Chat endpoints ═══════════════════════════════════════════

class ChatMsg(BaseModel):
    room_id: str
    player_id: str
    text: str

_chat_id_counter = 0


@app.post("/chat")
async def chat_send(msg: ChatMsg):
    global _chat_id_counter
    room = rooms.get(msg.room_id)
    if room is None:
        raise HTTPException(status_code=404, detail="Room not found")

    _chat_id_counter += 1
    entry = {
        "id": _chat_id_counter,
        "player_id": msg.player_id,
        "text": msg.text,
        "timestamp": datetime.utcnow().strftime("%H:%M")
    }
    if msg.room_id not in chat_messages:
        chat_messages[msg.room_id] = []
    chat_messages[msg.room_id].append(entry)

    # Trim to 200
    if len(chat_messages[msg.room_id]) > 200:
        chat_messages[msg.room_id] = chat_messages[msg.room_id][-200:]

    return {"status": "ok", "id": _chat_id_counter}


@app.get("/chat/poll")
async def chat_poll(room_id: str, last_id: int = 0):
    msgs = chat_messages.get(room_id, [])
    new_msgs = [m for m in msgs if m["id"] > last_id]
    return new_msgs


if __name__ == "__main__":
    import uvicorn
    print(f"=== LanEmulator Signaling Server v1.0.0 ===")
    print(f"VPN: {VPN_SUBNET}/24 | Max: {MAX_PLAYERS} | Timeout: {PLAYER_TIMEOUT.total_seconds():.0f}s")
    print(f"Discovery: UDP {DISCOVERY_PORT}")
    uvicorn.run("server:app", host="0.0.0.0", port=8000, reload=False)
else:
    # When imported by uvicorn (uvicorn server:app), start discovery
    threading.Thread(target=discovery_responder, daemon=True).start()
