"""
Wintun LAN Emulator — Signaling Server (Step 4)
FastAPI + uvicorn | In-memory room registry | Max 2 players per room
Virtual IP assignment (micro-DHCP): 10.13.37.1, 10.13.37.2, ...

Run: uvicorn server:app --host 0.0.0.0 --port 8000
"""
from fastapi import FastAPI, Request, Query, HTTPException
from pydantic import BaseModel
from datetime import datetime, timezone
from ipaddress import IPv4Address

app = FastAPI(title="LanEmulator Signaling Server")

# ── In-memory storage ─────────────────────────────────────────
# rooms: {
#   room_id: {
#     "next_ip": int,        # next virtual IP octet to assign (1-based)
#     "players": { player_id: {ip, udp_port, virtual_ip, joined} },
#     "created": datetime
#   }
# }
rooms: dict[str, dict] = {}

VPN_SUBNET = IPv4Address("10.13.37.0")


# ── Models ────────────────────────────────────────────────────

class RegisterRequest(BaseModel):
    player_id: str          # e.g. "Rustam"
    room_id: str            # e.g. "room123"
    udp_port: int = 51820   # port the player is listening on for UDP


# ── Routes ────────────────────────────────────────────────────

@app.post("/register")
async def register(req: RegisterRequest, request: Request):
    """
    Register a player in a room.
    Server records public IP, UDP port, and assigns a virtual IP.
    Max 2 players per room. First player gets 10.13.37.1, second gets 10.13.37.2.
    """
    room_id = req.room_id.strip().lower()
    player_id = req.player_id.strip()

    if not room_id or not player_id:
        raise HTTPException(400, "player_id and room_id are required")

    # Get or create room
    if room_id not in rooms:
        rooms[room_id] = {
            "next_ip": 1,
            "players": {},
            "created": datetime.now(timezone.utc)
        }

    room = rooms[room_id]

    # Enforce 2-player limit
    if len(room["players"]) >= 2 and player_id not in room["players"]:
        raise HTTPException(409, "Room is full (max 2 players)")

    # If player already registered, return their existing virtual IP
    if player_id in room["players"]:
        existing = room["players"][player_id]
        return {
            "status": "ok",
            "room_id": room_id,
            "virtual_ip": existing["virtual_ip"],
            "player_count": len(room["players"])
        }

    # Assign virtual IP: 10.13.37.{next_ip}
    virtual_ip = str(VPN_SUBNET + room["next_ip"])
    room["next_ip"] += 1

    # Register player
    public_ip = request.client.host if request.client else "unknown"
    room["players"][player_id] = {
        "ip": public_ip,
        "udp_port": req.udp_port,
        "virtual_ip": virtual_ip,
        "joined": datetime.now(timezone.utc).isoformat()
    }

    print(f"[REGISTER] {player_id} → room {room_id} | public={public_ip}:{req.udp_port} vpn={virtual_ip}")
    return {
        "status": "ok",
        "room_id": room_id,
        "virtual_ip": virtual_ip,
        "player_count": len(room["players"])
    }


@app.get("/poll")
async def poll(room_id: str = Query(..., description="Room ID to check")):
    """
    Check if a peer is available in the room.
    Returns all players with their public IP, UDP port, and virtual IP.
    """
    room_id = room_id.strip().lower()

    if room_id not in rooms:
        raise HTTPException(404, "Room not found")

    room = rooms[room_id]
    players = list(room["players"].values())

    if len(players) < 2:
        return {"status": "waiting", "player_count": len(players)}

    sorted_players = sorted(room["players"].items(), key=lambda x: x[1]["joined"])
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
            for pid, pinfo in sorted_players
        ]
    }


@app.get("/rooms")
async def list_rooms():
    """List all rooms (debug endpoint)."""
    return {
        room_id: {
            "player_count": len(info["players"]),
            "players": {
                pid: pinfo["virtual_ip"]
                for pid, pinfo in info["players"].items()
            },
            "created": info["created"].isoformat()
        }
        for room_id, info in rooms.items()
    }


@app.get("/health")
async def health():
    return {"status": "ok", "rooms": len(rooms)}


# ── Startup ───────────────────────────────────────────────────

if __name__ == "__main__":
    import uvicorn
    print("=== LanEmulator Signaling Server ===")
    print(f"VPN subnet: {VPN_SUBNET}/24")
    print("Endpoints:")
    print("  POST /register  — join a room (returns virtual_ip)")
    print("  GET  /poll      — check for peer (includes virtual_ip)")
    print("  GET  /rooms     — list all rooms (debug)")
    print("  GET  /health    — health check")
    uvicorn.run(app, host="0.0.0.0", port=8000)
