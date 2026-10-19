# Gungeon Online

Online co-op for Enter the Gungeon with a camera for each player.

- Each player has their own camera, HUD and controller, on their own PC.
- Steam, GOG and Epic copies can play together. The game code is the same on all of them.
- Your own character never waits on the network. The host runs the real game and every other machine follows it, so the two games cannot drift apart. A once-a-second check finds and repairs any mismatch.
- Any character for any player.

Status: in development. See `docs/reports/` for what has been tested and how.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64, 5.4.23 or newer) into your Enter the Gungeon folder, or run `tools/install.ps1 -GameDir "<your game folder>"`.
2. Copy `GungeonOnline.dll` into `BepInEx/plugins/GungeonOnline/`.
3. Start the game once. A config file appears at `BepInEx/config/gungeononline.cfg`.

Everyone in a session needs the same mod version.

## Play

Press **F8** in game.

- **Host:** press *Host a session*. Send your friend the invite code. On Steam you can also press *Invite a Steam friend*.
- **Join:** paste the host's code (or `ip:port`) and press *Join*.
- If the join does not go through, the joiner sends the host their *reply code*, and the host pastes it into *Let them in*. This opens a path through both routers.
- If both routers are strict, use a VPN such as Tailscale or Radmin VPN and join with `ip:port`.

Then each player picks a character in the Breach and the host walks into the Gungeon.

## How it plays differently from local co-op

- Cameras are independent. Nobody gets pulled into the other player's fight. While one room is in combat, other rooms with enemies stay shut (config: `SecondFightLock`).
- Pausing opens your menu but the game keeps running for your partner (config: `PauseFreezesEveryone`).
- Talking to an NPC, a shrine or a shop only takes over the screen of the player who is talking.
- Hits on your character are decided by your own PC. If you saw the bullet miss, it missed.

The full list is in `docs/COOP-INTERACTIONS.md`.

## Settings (F8 window or the config file)

| Setting | Default | What it does |
|---|---|---|
| Other player smoothing | Auto | Buffer for other players' input. Auto sizes it from measured jitter. Your own character is never delayed. |
| Correction speed | 0.25 | How fast small position differences from the host are blended out. |
| WorldSnapshotHz | 120 | How often the host sends enemy positions. |
| Port | 47624 | UDP port used when hosting. |
| MaxPlayers | 2 | The game supports 2 players today. The netcode is built for up to 4. |

## Building

```
dotnet build src/GungeonOnline -c Release -p:GameDir="<game folder with BepInEx>"
```


## License

MIT
