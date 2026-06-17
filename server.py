"""
Wintun LAN Emulator — Signaling Server (Step 3.1)
FastAPI + uvicorn | In-memory room registry | Max 2 players per room

Run: uvicorn server:app --host 0.0.0.0 --port 8000
"""
from fastapi import FastAPI, Request, Query, HTTPException
from pydantic import BaseModel
from datetime import datetime, timezone
import uuid

app = FastAPI(title="LanEmulator Signaling Server")

# ── In-memory storage ─────────────────────────────────────────
# rooms: { room_id: { "players": { player_id: {ip, port, joined} }, "created": datetime } }
rooms: dict[str, dict] = {}


# ── Models ────────────────────────────────────────────────────

class RegisterRequest(BaseModel):
    player_id: str    # e.g. "Rustam"
    room_id: str      # e.g. "room123"
    udp_port: int = 51820  # port the player is listening on for UDP


# ── Routes ────────────────────────────────────────────────────

@app.post("/register")
async def register(req: RegisterRequest, request: Request):
    """
    Register a player in a room.
    Server records the player's public IP (from TCP connection) and UDP port.
    Max 2 players per room.
    """
    room_id = req.room_id.strip().lower()
    player_id = req.player_id.strip()

    if not room_id or not player_id:
        raise HTTPException(400, "player_id and room_id are required")

    # Get or create room
    if room_id not in rooms:
        rooms[room_id] = {"players": {}, "created": datetime.now(timezone.utc)}

    room = rooms[room_id]

    # Enforce 2-player limit
    if len(room["players"]) >= 2 and player_id not in room["players"]:
        raise HTTPException(409, "Room is full (max 2 players)")

    # Register player
    public_ip = request.client.host if request.client else "unknown"
    room["players"][player_id] = {
        "ip": public_ip,
        "udp_port": req.udp_port,
        "joined": datetime.now(timezone.utc).isoformat()
    }

    print(f"[REGISTER] {player_id} → room {room_id} | {public_ip}:{req.udp_port}")
    return {
        "status": "ok",
        "room_id": room_id,
        "player_count": len(room["players"])
    }


@app.get("/poll")
async def poll(room_id: str = Query(..., description="Room ID to check")):
    """
    Check if a peer is available in the room.
    Returns peer's IP and UDP port if found, otherwise {"status": "waiting"}.
    """
    room_id = room_id.strip().lower()

    if room_id not in rooms:
        raise HTTPException(404, "Room not found")

    room = rooms[room_id]
    players = list(room["players"].values())

    if len(players) < 2:
        return {"status": "waiting", "player_count": len(players)}

    # Return the most recently joined player (the peer)
    # Sort by join time, return the OTHER player
    sorted_players = sorted(room["players"].items(), key=lambda x: x[1]["joined"])
    # If caller is the first player, return second; if caller is second, return first
    return {
        "status": "ready",
        "players": [
            {
                "player_id": pid,
                "ip": pinfo["ip"],
                "udp_port": pinfo["udp_port"]
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
            "players": list(info["players"].keys()),
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
    print("Endpoints:")
    print("  POST /register  — join a room")
    print("  GET  /poll      — check for peer")
    print("  GET  /rooms     — list all rooms (debug)")
    print("  GET  /health    — health check")
    uvicorn.run(app, host="0.0.0.0", port=8000)
