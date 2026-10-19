# Clean-room statement

Enter The Gungeon Online Co-Op Mod was written from scratch.

- **No code from any other Gungeon multiplayer mod is in this repository.** That includes GungeonTogether. Nothing was copied, translated, ported or adapted from it, and no assets, files, data or configuration from it were used.
- Before starting, I looked at GungeonTogether once to see what already existed for Enter the Gungeon. I decided not to build on it. Its code never entered this repository or the build, and nothing here depends on it.
- The design came from how Enter the Gungeon itself behaves. Enter The Gungeon Online Co-Op Mod hooks the game at runtime through [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX), the standard Unity modding tools. The networking (reliable UDP, NAT traversal, invite codes, input streaming, prediction and reconciliation, world replication, desync checks) is original code in `src/GungeonOnline/`.
- The full history is in git, from the empty repository onward. The milestone reports in `docs/reports/` record each step as it was built and tested.

## What this repository does not contain

- No Enter the Gungeon game files, code or art. You need your own copy of the game (Steam, GOG or Epic).
- No code from other Gungeon mods.

## Third-party software

The download bundles BepInEx (the mod loader), unchanged, under its own license. See `THIRD-PARTY-NOTICES.md`.

Enter the Gungeon is a trademark of Dodge Roll and Devolver Digital. Enter The Gungeon Online Co-Op Mod is an unofficial fan project and is not affiliated with or endorsed by them.
