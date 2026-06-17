# Goldberg Steam Emulator

Place `steam_api64.dll` from Goldberg Emulator in this folder.

## Where to get it
- GitLab: https://gitlab.com/Mr_Goldberg/goldberg_emulator
- Or from any pirated game that already uses Goldberg

## steam_settings/ folder
Some games require `steam_settings/steam_appid.txt` with the game's Steam AppID
(e.g. Subnautica = 264710). Place that file in `goldberg/steam_settings/steam_appid.txt`.

The LanEmulator will copy the entire `steam_settings/` folder to the game directory.

If `steam_settings/` doesn't exist here, the program creates a template file
in the game folder — edit it with the correct AppID before launching.

## What the LanEmulator does
1. Backup original `steam_api64.dll` → `steam_api64.dll.bak`
2. Copy `goldberg/steam_api64.dll` → game directory
3. Copy `goldberg/steam_settings/` → game directory (or create template)
4. Write `GoldbergSteamEmu.ini` with `[Networking] ip=<peer VPN IP>`
