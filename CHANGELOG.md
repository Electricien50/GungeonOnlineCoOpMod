# Changelog

## 1.0.6

**Fixes**
- Hosting from inside a run you started alone no longer lets a friend join a game that cannot sync (they stayed in the Breach while you saw a frozen copy of them). That run's floors were not built in a way your friend's game can repeat, so the game now says to start online play from the Breach. Hosting in the Breach and then starting the run works as before, and friends can still join a run that was started online.

## 1.0.5

**Fixes**
- Keyboard and mouse now work online. With two players the game turned keyboard and mouse off for player one, so keyboard players could not move or pause once a run started.
- Typing an invite code and pressing Enter now joins. Before, Enter did nothing in the code box; a code that is not valid now says so.

**Improvements**
- Lower input delay. Your friend's presses are now timed by when they actually arrive instead of by the next frame that happens to read them, so their character reacts sooner, on every frame rate and connection. Measured press to use: about 50 ms down to 20 ms on the same network, 47 to 29 ms at 25 ms ping, 76 to 57 ms at 70 ms ping.
- Your friend's character no longer drifts out of place or gets snapped back. Every input your friend's game sends now says exactly where their character was, and your game puts it there, so both screens always agree on where everyone stands without any small corrections.
- When the host's game moves a player on purpose (the Classic pull into a fight, a warp, a boss intro), that player's own game now makes the same move.
- If your friend's connection pauses, their character now stands still until it comes back instead of running on with the last button held.

## 1.0.4

**Fixes**
- After a network dropout of roughly 10 to 20 seconds (a Wi-Fi hiccup, say), the game could stay connected but stop sharing the world: enemies, pickups and doors stopped updating for the friend for the rest of the session. Late packets from before the dropout made the host think lost messages had arrived. The connection now catches up by itself within a few seconds, and if it ever does get stuck, it reconnects and restores the run on its own.

## 1.0.3

**Fixes**
- Friends can now get in when the host's router or firewall was blocking the join. The joining game asks the host's game, through the mod's small relay, to open a path, and the next try goes through. Only the two public addresses are sent, and the relay forgets them after 30 seconds.
- If the host still can't be reached, the joining game keeps trying for another minute and copies your reply code for you, so the host can pick Let in friend. Before, the reply code was never shown, and it stopped working as soon as the join gave up.
- The host now gets a message when someone tries to join with a different version of the mod or the game, or when the game is full.
- Send report now includes what the connection setup saw, so join problems are easier to track down.

## 1.0.2

**New**
- Couch co-op with any character: on one PC, player two joins at the Cultist as usual, then talks to any Gungeoneer in the Breach to become them, just like player one. The Gungeoneers stay out in the Breach during couch co-op instead of disappearing.

**Fixes**
- If the guest's game crashed while the host was loading the next floor, the host could get stuck on the loading screen.
- After a crash, the friend who came back could stay frozen: leftover network traffic from the crashed game was mixed into the new connection. Every packet now carries its session, and old ones are ignored.
- Falling into a pit while your friend rides a minecart no longer leaves you hanging mid-fall until they get off.

## 1.0.1

**Fixes**
- The guest now sees every enemy bullet. Walking into a boss room after the fight had started cleared the boss's bullets on the guest's screen while they kept flying for the host. Bullets already in the air when the guest finishes loading a floor are sent too.
- Rejoining as the Paradox or the Gunslinger no longer charges Hegemony Credits again.
- A screen that stays black while you can still move now fades back in by itself within a few seconds.
- When both players go down, both games now end the run (the guest could keep walking around as a ghost).
- The ping shown in the menu and the connection warning is steady now (it used to jump with every frame hitch).

**New**
- *Send report* (was Save report) sends the report to me as well as saving it to your Desktop. Invite codes, IP addresses, your Windows user name and Steam ids are removed first.
- The game tells you when a new version is out: one check at launch, nothing about you is sent. Turn it off in Settings, Help and advanced.

**Updating from 1.0:** run the installer again (or copy the files over). Your settings and saved runs are kept.

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

**Fixes**
- A character pick sent before the connection was fully up could get lost and leave both players in the Breach: the pick is now re-sent until the host has it.
- Reliable messages can no longer stall: a message that arrived but could not be used is sent again.
- Minecarts: the cart you drive is yours on every machine (no more drift while riding).
- Sync checks no longer raise false alarms at very high ping or right after a drop.
- A friend on a slow PC gets more time to load a floor before being sent it again.
- Safety net: if your character is ever left without control or stuck mid-fall for no reason, control comes back on its own.
- Locator only shows once your friend is fully off screen (no overlap with the name tag).
- Lower cost per frame (health and money sync no longer rebuilds text every frame).

**Known issue**
- In some fights the guest does not see a few of the enemies' bullets. Those bullets can't hit the guest (hits are decided on the guest's own screen), and everything else stays in sync. Fixed in 1.0.1.

**Tools**
- *Save report* writes a report to your Desktop. `metrics.jsonl` records connection numbers every 5 seconds.
