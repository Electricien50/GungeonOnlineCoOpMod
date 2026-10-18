# Co-op interactions with independent cameras

Vanilla Enter the Gungeon co-op assumes one screen, one camera and one machine. This document lists every
place where that assumption shows up and what Gungeon Online does instead. It is grounded in the decompiled
game code (Assembly-CSharp, identical on Steam and GOG).

## The rule that makes most of this easy

The host runs the one real simulation. Every item, synergy, enemy and room script that reads
`GameManager.AllPlayers`, `GetOtherPlayer` or `SecondaryPlayer` (168 files) runs on the host exactly as in
local co-op, so its gameplay result is already correct. What needs netplay-aware patches is only what is
*per screen* or *per person*: cameras, UI, input freezes, audio, rumble, pause, and persistent saves.

## Decisions

| Area | Vanilla local co-op | Gungeon Online |
|---|---|---|
| Camera | One camera averages both players (`CameraController.GetCoreCurrentBasePosition`) | Each machine follows only its own player (`IgnoredByCamera` on everyone else) |
| Partner pulled into fights | `HandleCoopSpecificTimers` warps the outside player into a sealed room after 1 s | Off. Doors stay shut for the outside player. Second-fight lock: while a room is sealed, other uncleared rooms do not start |
| Off-screen clamp | Player pushed/warped back inside the shared camera (PlayerController.Update, ghost block) | Off |
| Pause menu | `GameManager.PauseRaw` sets time scale 0 for the whole world | Opens locally, world keeps running, banner "game is still running". Config option `PauseFreezesEveryone` (default off) |
| Ammonomicon, map | Local UI | Local UI on the machine that opened it; world keeps running |
| NPC dialogue, shops, shrines, crest doors, Ark, etc. (≈40 scripts call `SetInputOverride` on the other player) | Freezes **both** players and moves the shared camera | Freezes only the player who interacted. Camera moves only on that player's screen. The partner keeps playing |
| Dialogue UI | Text boxes over the NPC, option list on screen | Host runs the conversation with the talking player's input (already routed through their input device). Text box show/clear and option lists replicate to the talking player's screen only |
| Boss intros, boss kill cam | Gather both players, camera pan, slow motion | Kept for both: both players are gathered, both screens play the intro and kill cam, slow motion applies to the world |
| Time-scale effects (Superhot, Sunglasses, table tech, boss deaths, Turbo/Challenge speed) | Global `Time.timeScale` | Host's time scale replicated; client applies the same scale |
| Floor exit | Elevator requires both players | Kept |
| Pasts, Punchout, Resourceful Rat / Metal Gear Rat room, final sequences | Scripted, shared camera | Treated like boss intros: both gathered, both screens follow the script. Each gets its own scenario test (M6) |
| Wallet (casings, keys) | Shared: P2 uses P1's `carriedConsumables` | Kept shared (vanilla co-op design). Host-owned, replicated to both HUDs |
| Blanks, health, armor, guns, items | Per player | Per player, host-owned, replicated |
| Pickups and gun drops | Whoever touches or interacts | Same. Client sends interact/touch requests; host decides, first arrival wins |
| Chests | Either player opens; contents for both | Same. Co-op revive chest behaviour kept |
| Death / ghost | Ghost clamped to the shared camera and warped to partner | Ghost has its own camera and roams freely; revive rules unchanged |
| HUD | P1 left, P2 right on one screen | Same layout on both screens (host left, guest right), both full size, fed from replicated state |
| Item/gun pickup banners, reload bar, "new item" notes | One screen | Shown only on the owning player's screen |
| Screen shake | Shared camera, global | Applied on the screen whose player is in range of the source |
| Rumble | `BraveInput.DoVibrationForAllPlayers` rumbles every pad | Local controller only, for events that involve that player |
| Audio listener | `listenerId` 0 and 1 per player | Each machine listens at its own player |
| Minimap | Rooms revealed by either player | Same shared reveal; both player icons on both maps |
| Enemy difficulty | Co-op: 1.25x enemy health, 0.9x bullet speed | Same multipliers, adjustable in config |
| Character choice | P2 forced to the Cultist | Any character for both, duplicates allowed |
| Item pool and unlocks | One save | The host's save decides the item pool for the run |
| Persistent progress | One save file | Each player's own save gets credit: Hegemony credits, kill counts, boss flags, unlock events replicated from the host to the guest's save |
| Mid-run save, quick restart | One save | Mid-run save disabled in netplay; host quick restart restarts both |
| Mouse users | P2 cursor handled by `GameCursorController` | Each machine handles its own cursor; aim sent as a vector |
| Off-screen enemy logic on host | n/a | Verified: `tk2dSpriteAnimator` keeps animation time and events running off screen; only the sprite mesh update is skipped. Rooms the host cannot see simulate normally |

## Where these patches live

- `Camera/IndependentCamera.cs`: camera ignore, no warp/clamp, screen shake routing.
- `Game/InteractionScope.cs`: rewrites `SetInputOverride` calls on the other player to no-ops in netplay,
  routes camera override calls (≈39 sites) to the involved player's screen, keeps boss-intro scripts global.
- `Game/PauseScope.cs`: local pause, Ammonomicon, map.
- `Game/ProgressSync.cs`: guest save credit.
- `Replication/UiEvents.cs`: dialogue boxes, option lists, banners, notifications per owning player.
- `Input/Rumble.cs`: local-only vibration.

## Test scenarios (one per row, added as the matching milestone lands)

`dialogue-one-player`, `shop-while-partner-fights`, `pause-no-freeze`, `boss-intro-both`, `ghost-roam`,
`superhot-timescale`, `elevator-both`, `pickup-race`, `revive-chest`, `guest-save-credit`.
