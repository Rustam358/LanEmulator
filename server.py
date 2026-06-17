"""
Wintun LAN Emulator — Signaling Server v1.0.0
FastAPI + uvicorn | In-memory room registry | Up to 20 players per room
Virtual IP assignment (micro-DHCP): 10.13.37.1 … 10.13.37.254
IP reuse: players not seen for 60s are dropped; their IPs are recycled.

Run: uvicorn server:app --host 0.0.0.0 --port 8000
"""
from fastapi import FastAPI, Request, Query, HTTPException
from pydantic import BaseModel
from datetime import datetime, timezone, timedelta
from ipaddress import IPv4Address

app = FastAPI(title="LanEmulator Signaling Server")

MAX_PLAYERS = 20
VPN_SUBNET = IPv4Address("10.13.37.0")
PLAYER_TIMEOUT = timedelta(seconds=60)       # drop player after 60s without re-register
IP_RECYCLE_TIMEOUT = timedelta(seconds=120)  # recycle IP after 120s without re-register

# ── In-memory storage ─────────────────────────────────────────
rooms: dict[str, dict] = {}


# ── Models ────────────────────────────────────────────────────

class RegisterRequest(BaseModel):
    player_id: str
    room_id: str
    udp_port: int = 51820


# ── Helpers ───────────────────────────────────────────────────

def _cleanup_room(room: dict, now: datetime):
    """Remove timed-out players from a room."""
    stale = [pid for pid, pinfo in room["players"].items()
             if now - datetime.fromisoformat(pinfo["last_seen"]) > PLAYER_TIMEOUT]
    for pid in stale:
        print(f"[TIMEOUT] {pid} removed from room (no re-register for 60s)")
        del room["players"][pid]


def _find_free_ip(room: dict) -> int:
    """Find next available IP index, recycling timed-out IPs first."""
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


# ── Routes ────────────────────────────────────────────────────

@app.post("/register")
async def register(req: RegisterRequest, request: Request):
    """
    Register a player in a room (or re-register to refresh presence).
    Assigns a unique virtual IP. Up to 20 active players per room.
    """
    room_id = req.room_id.strip().lower()
    player_id = req.player_id.strip()

    if not room_id or not player_id:
        raise HTTPException(400, "player_id and room_id are required")

    now = datetime.now(timezone.utc)

    if room_id not in rooms:
        rooms[room_id] = {
            "players": {},
            "created": now
        }

    room = rooms[room_id]

    # Cleanup timed-out players
    _cleanup_room(room, now)

    # Use X-Forwarded-For behind reverse proxy, else direct client IP
    fwd = request.headers.get("X-Forwarded-For")
    public_ip = (
        (fwd.split(",")[0].strip() if fwd else None) or
        (request.client.host if request.client else "unknown")
    )

    # Re-register: refresh last_seen, return existing IP
    if player_id in room["players"]:
        existing = room["players"][player_id]
        existing["ip"] = public_ip
        existing["udp_port"] = req.udp_port
        existing["last_seen"] = now.isoformat()
        return {
            "status": "ok",
            "room_id": room_id,
            "virtual_ip": str(VPN_SUBNET + existing["ip_index"]),
            "player_count": len(room["players"])
        }

    # Check limit
    active = sum(1 for p in room["players"].values()
                 if now - datetime.fromisoformat(p["last_seen"]) <= PLAYER_TIMEOUT)
    if active >= MAX_PLAYERS:
        raise HTTPException(409, f"Room is full (max {MAX_PLAYERS} active players)")

    # Assign virtual IP
    ip_index = _find_free_ip(room)
    virtual_ip = str(VPN_SUBNET + ip_index)

    room["players"][player_id] = {
        "ip": public_ip,
        "udp_port": req.udp_port,
        "virtual_ip": virtual_ip,
        "ip_index": ip_index,
        "last_seen": now.isoformat(),
        "joined": now.isoformat()
    }

    print(f"[REGISTER] {player_id} → room {room_id} | public={public_ip}:{req.udp_port} vpn={virtual_ip} ({active + 1}/{MAX_PLAYERS})")
    return {
        "status": "ok",
        "room_id": room_id,
        "virtual_ip": virtual_ip,
        "player_count": len(room["players"])
    }


@app.get("/poll")
async def poll(room_id: str = Query(..., description="Room ID")):
    """Return all active players (last_seen within timeout)."""
    room_id = room_id.strip().lower()

    if room_id not in rooms:
        raise HTTPException(404, "Room not found")

    room = rooms[room_id]
    now = datetime.now(timezone.utc)

    # Filter to active players
    active = {
        pid: pinfo
        for pid, pinfo in room["players"].items()
        if now - datetime.fromisoformat(pinfo["last_seen"]) <= PLAYER_TIMEOUT
    }

    if len(active) < 2:
        return {"status": "waiting", "player_count": len(active), "players": []}

    return {
        "status": "ready",
        "player_count": len(active),
        "players": [
            {
                "player_id": pid,
                "ip": pinfo["ip"],
                "udp_port": pinfo["udp_port"],
                "virtual_ip": pinfo["virtual_ip"]
            }
            for pid, pinfo in sorted(active.items(), key=lambda x: x[1]["joined"])
        ]
    }


@app.post("/leave")
async def leave(room_id: str = Query(...), player_id: str = Query(...)):
    """Explicitly leave a room (immediate removal)."""
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
    """List all rooms (debug)."""
    return {
        rid: {
            "player_count": len(info["players"]),
            "players": {pid: p["virtual_ip"] for pid, p in info["players"].items()},
            "created": info["created"].isoformat()
        }
        for rid, info in rooms.items()
    }


@app.get("/health")
async def health():
    return {"status": "ok", "rooms": len(rooms)}


if __name__ == "__main__":
    import uvicorn
    print(f"=== LanEmulator Signaling Server v1.0.0 ===")
    print(f"VPN subnet: {VPN_SUBNET}/24 | Max players/room: {MAX_PLAYERS}")
    print(f"Player timeout: {PLAYER_TIMEOUT.total_seconds():.0f}s | IP recycle: {IP_RECYCLE_TIMEOUT.total_seconds():.0f}s")
    print("Endpoints: POST /register | GET /poll | POST /leave | GET /rooms | GET /health")
    uvicorn.run(app, host="0.0.0.0", port=8000)
