# Goldberg Steam Emulator

Place `steam_api64.dll` from Goldberg Emulator in this folder.

## Where to get it
- GitLab: https://gitlab.com/Mr_Goldberg/goldberg_emulator
- Or from any pirated game that already uses Goldberg

## Usage
The LanEmulator will automatically:
1. Backup the game's original `steam_api64.dll` → `steam_api64.dll.bak`
2. Copy this folder's `steam_api64.dll` into the game directory
3. Write `GoldbergSteamEmu.ini` with the peer's virtual IP in `[Networking]`
