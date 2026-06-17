"""
Wintun LAN Emulator — Signaling Server
FastAPI + uvicorn | In-memory room registry | Up to 20 players per room
Virtual IP assignment (micro-DHCP): 10.13.37.1 … 10.13.37.254

Run: uvicorn server:app --host 0.0.0.0 --port 8000
"""
from fastapi import FastAPI, Request, Query, HTTPException
from pydantic import BaseModel
from datetime import datetime, timezone
from ipaddress import IPv4Address

app = FastAPI(title="LanEmulator Signaling Server")

MAX_PLAYERS = 20
VPN_SUBNET = IPv4Address("10.13.37.0")

# ── In-memory storage ─────────────────────────────────────────
rooms: dict[str, dict] = {}


# ── Models ────────────────────────────────────────────────────

class RegisterRequest(BaseModel):
    player_id: str
    room_id: str
    udp_port: int = 51820


# ── Routes ────────────────────────────────────────────────────

@app.post("/register")
async def register(req: RegisterRequest, request: Request):
    """
    Register a player in a room. Assigns a unique virtual IP.
    Up to 20 players per room.
    """
    room_id = req.room_id.strip().lower()
    player_id = req.player_id.strip()

    if not room_id or not player_id:
        raise HTTPException(400, "player_id and room_id are required")

    if room_id not in rooms:
        rooms[room_id] = {
            "next_ip": 1,
            "players": {},
            "created": datetime.now(timezone.utc)
        }

    room = rooms[room_id]

    # Check limit
    if len(room["players"]) >= MAX_PLAYERS and player_id not in room["players"]:
        raise HTTPException(409, f"Room is full (max {MAX_PLAYERS} players)")

    # Re-register: return existing IP
    if player_id in room["players"]:
        existing = room["players"][player_id]
        return {
            "status": "ok",
            "room_id": room_id,
            "virtual_ip": str(VPN_SUBNET + existing["ip_index"]),
            "player_count": len(room["players"])
        }

    # Assign virtual IP
    ip_index = room["next_ip"]
    room["next_ip"] += 1
    virtual_ip = str(VPN_SUBNET + ip_index)

    public_ip = request.client.host if request.client else "unknown"
    room["players"][player_id] = {
        "ip": public_ip,
        "udp_port": req.udp_port,
        "virtual_ip": virtual_ip,
        "ip_index": ip_index,
        "joined": datetime.now(timezone.utc).isoformat()
    }

    print(f"[REGISTER] {player_id} → room {room_id} | public={public_ip}:{req.udp_port} vpn={virtual_ip} ({len(room['players'])}/{MAX_PLAYERS})")
    return {
        "status": "ok",
        "room_id": room_id,
        "virtual_ip": virtual_ip,
        "player_count": len(room["players"])
    }


@app.get("/poll")
async def poll(room_id: str = Query(..., description="Room ID")):
    """Return all players in the room with their public IP, UDP port, and virtual IP."""
    room_id = room_id.strip().lower()

    if room_id not in rooms:
        raise HTTPException(404, "Room not found")

    room = rooms[room_id]
    players = list(room["players"].items())

    if len(players) < 2:
        return {"status": "waiting", "player_count": len(players), "players": []}

    return {
        "status": "ready",
        "player_count": len(players),
        "players": [
            {
                "player_id": pid,
                "ip": pinfo["ip"],
                "udp_port": pinfo["udp_port"],
                "virtual_ip": pinfo["virtual_ip"]
            }
            for pid, pinfo in sorted(players, key=lambda x: x[1]["joined"])
        ]
    }


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
    print(f"=== LanEmulator Signaling Server ===")
    print(f"VPN subnet: {VPN_SUBNET}/24 | Max players/room: {MAX_PLAYERS}")
    print("Endpoints: POST /register | GET /poll | GET /rooms | GET /health")
    uvicorn.run(app, host="0.0.0.0", port=8000)
