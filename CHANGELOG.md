# Changelog

## 1.0 (first public release)

**Play**
- Online co-op for 2 players, each on their own PC with their own controller, camera and HUD.
- Any Gungeoneer for both players, duplicates allowed.
- Steam, GOG and Epic copies play together.
- Host-authoritative netcode with local prediction: zero added input delay on your own character, and a once-a-second sync check that repairs any difference on the spot.

**Easy to start**
- One-click installer that finds the game on Steam (every library), GOG and Epic.
- *Play Online* in the game's pause menu, plus Back + Start on a controller, plus F8.
- Controller-first menu. Invite codes are copied to the clipboard; joining reads the code from the clipboard.
- UPnP, hole punching, a reply-code fallback and `ip:port` for VPNs. Steam friends can be invited from the menu.
- The Breach door waits until everyone has picked a character.

**A run is never lost**
- Short drops (up to 20 s) are bridged. Long loads no longer count as silence.
- A dropped or crashed player is waited for with no time limit and gets their character, items and spot back.
- The host backs up the run at every floor and every 2 minutes; *Resume saved run* after a host crash.
- Quick restart is off online.

**Fair and consistent**
- Hits and pit falls are decided by the player they happen to.
- The exit elevator leaves on every PC together.
- Minecarts: the cart you ride responds instantly on your screen.
- Each player's own aim assist setting is used for their character everywhere.
- Hegemony credits, kills and unlocks earned in the run go into both players' saves.
- One fight at a time (optional), local pause, NPC conversations only take over the talking player's screen.

**Tools**
- *Save report* writes a report to your Desktop. `metrics.jsonl` records connection numbers every 5 seconds.
