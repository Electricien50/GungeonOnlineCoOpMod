# Architecture

## Model

The host runs the real game. Every other machine runs the same floor (same run seed) but only its own player
is simulated for real; everything else follows the host. The client never decides an outcome the host
disagrees with, so the two games cannot drift apart. A once-a-second summary check finds any leftover
difference and repairs it with a full resync.

Each machine treats its own player as the game's player 1 (PrimaryPlayer). Remote players are added the
way vanilla co-op adds player 2. Network slots are mapped to the game's player numbers in one place,
`Game/PlayerSlots.cs`.

## Frame order (no added frames)

1. Start of frame, before InControl polls devices (`Input/InputPump`): drain the socket, feed remote players'
   virtual controllers with the input that is due.
2. Right after the poll: sample your own player and send the input packet immediately.
3. Gameplay and physics run.
4. End of frame (`DefaultExecutionOrder` 31000 and up): player reconciliation, world snapshots, events, then
   `NetFlush` (32000) sends everything.

## Who decides what

| Thing | Decided by | Mechanism |
|---|---|---|
| Your movement, aim, shots | You, instantly | Predicted locally; host replays your input on your clock and corrects drift |
| Hits on your character | Your machine | Hit report to host; host applies to its copy |
| Enemies, their AI and bullets | Host | Spawn/death/damage events, snapshots, bullet spawn + drift corrections |
| Damage to enemies | Host | Host copies of player bullets deal damage; client copies are cosmetic |
| Rooms, doors, waves, rewards | Host | Seal events; client never starts combat on its own |
| Items on the ground, chests, shops | Host (its save sets the item pool) | Copies on clients; pickups and purchases are requests |
| Inventories, health, wallet | Host | Pickup events plus full reconciliation |
| Floor changes and run seed | Host | Load message; client loads only on the host's word |
| Companions | Each machine | Spawned by each machine's copy of the player's items |
| Breach (lobby) | Each machine | Built from each player's own save |

## Files

- `Net/`: transport-agnostic reliable UDP (`Connection`), sockets (UDP, Steam P2P, simulated link, composite),
  handshake for N players (`NetNode`), STUN, UPnP, invite codes. No Unity dependency; tested in `tests/NetTests`.
- `Session/`: session lifecycle, lobby and roster, overlay, config, Steam lobby invites.
- `Input/`: semantic input frames, virtual controller, fixed bindings, poll hooks.
- `Replication/`: input streams, player reconciliation, enemies and rooms, bullets, vitals, pickups and chests,
  inventories, shops, checksums.
- `Game/`: game-side rules for online play (slots, run sync, pause, conversation presentation, second-fight lock,
  background running).
- `Camera/`: independent cameras.
- `Dev/`: test harness hooks (inert unless the harness sets `GO_SAVE_DIR`).

## Wire format

Datagram: `u16 magic "GO" | u8 type | ...`. Data packets carry `u16 seq | u16 ack | u64 ackBits | u16 cumulative
reliable ack` followed by messages. Unreliable message: `u8 channel | varint len | payload`. Reliable message:
`u8 channel|0x80 | u16 id | varint len | payload`; 0x40 in the channel marks "more fragments follow".

Channels: 0 Control (floor loads), 1 Input, 2 Player snapshot, 3 Events, 4 Lobby, 5 World snapshot.
Event type ranges: 1-9 world/rooms, 20-29 bullets, 40-49 vitals and inventory, 50-59 pickups and chests,
60-69 shops, 70-79 checksums.
