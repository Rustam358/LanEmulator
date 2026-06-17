# LanEmulator

P2P virtual LAN emulator for gaming — connect to friends through the internet
as if you were on the same local network.

## How it works

1. You and a friend run the program, join the same room
2. A Signaling Server exchanges your IPs
3. UDP Hole Punching pierces through NAT
4. A virtual network adapter (Wintun) is created with a unique VPN IP
5. You can ping each other, play LAN games, browse shared folders

## Two modes

### [1] Steam Game (Goldberg)
Auto-patches pirated Steam games to play over the virtual LAN.
- Backs up original `steam_api64.dll`
- Copies Goldberg emulator DLL
- Writes `GoldbergSteamEmu.ini` with peer's VPN IP
- Auto-detects Steam AppID (or looks it up online)
- Launches the game automatically

### [2] Pure LAN
VPN only — no game files touched. Launch your game manually.
Works with any LAN-capable game: Warcraft 3, Minecraft, C&C, etc.

## Quick start

1. Run the Signaling Server:
```bash
pip install -r requirements.txt
uvicorn server:app --host 0.0.0.0 --port 8000
```
(Deploy to PythonAnywhere/Render for internet play)

2. Both players run `wintun-poc.exe` (Administrator required)
3. Enter the same Room ID
4. Friend's IP is discovered automatically
5. VPN is established — you're on the same virtual LAN!

## Building

```bash
dotnet build -c Release
```
Requires: .NET 8 SDK, Windows x64, wintun.dll next to executable.

## Tech stack
- C# / .NET 8 — client (Wintun P/Invoke, UDP transport, Goldberg patcher)
- Python / FastAPI — signaling server (room management, virtual DHCP)
- Wintun 0.14.1 — WireGuard virtual adapter driver
- Goldberg Emulator — Steam API emulation for LAN play
