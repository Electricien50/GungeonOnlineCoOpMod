# Contributing

Bug reports, logs and pull requests are welcome.

## Reporting a bug

1. Open the online menu and pick **Save report**. A text file appears on your Desktop.
2. Open an issue with what happened, roughly when, and attach the report from both players if you can.

## Building

You need the .NET SDK (any recent version builds the `net35` target) and a copy of Enter the Gungeon with BepInEx 5 installed.

```
dotnet build src/GungeonOnline -c Release -p:GameDir="<game folder with BepInEx>"
```

The plugin lands in `src/GungeonOnline/bin/Release/GungeonOnline.dll`. Copy it to `BepInEx/plugins/GungeonOnline/`.

## Tests

- `dotnet run --project tests/NetTests -c Release`: the network layer (reliability, fragmentation, handshake, timeouts).

## Ground rules

- **Original code only.** Do not paste or port code from other Enter the Gungeon mods, and never commit game files or decompiled game code. See `docs/CLEAN-ROOM.md`.
- The host decides outcomes; other machines predict their own player and follow the host for everything else. Read `docs/ARCHITECTURE.md` before changing replication.
- Every co-op difference from vanilla goes in `docs/COOP-INTERACTIONS.md`.
- Player-facing text is plain language (no "slot", "rtt", "reconcile").
