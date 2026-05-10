# All hands onboard

A BepInEx mod for Valheim that turns the ship's Slow speed into a real rowing minigame. Up to six players can take rower slots and push the ship by alternating L/R strokes in time with a metronome.

## What it does

- Each ship type has rower slots (Karve 2, Longship 4, Drakkar 6, Raft none).
- In Slow mode, sitting on the ship lets you claim a slot.
- Alternate the left and right keys to row. Strokes are scored against a target interval:
  - **PERFECT / GOOD / SLOW / TOO FAST / WRONG SIDE** verdicts
  - Streaks of perfect strokes grant a tempo bonus, then a ship-wide **Mastery** speed bonus past 20 in a row
  - When several players row in phase, a **Crew Sync** bonus applies on top
- HUD widget in the lower-left shows L/R indicators, current streak, the ship's smoothed multiplier, and the latest verdict.

## Install

1. Install [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) for Valheim.
2. Drop `AllHandsOnboard.dll` into `<Valheim>/BepInEx/plugins/`.
3. Launch the game once to generate the config file at `<Valheim>/BepInEx/config/com.allhands.onboard.cfg`.

All players in a session need the mod for slot/tempo sync to work.

## Controls

Defaults (rebindable in the config):

- **Keyboard:** `LeftArrow` / `RightArrow`
- **Gamepad:** `LT` / `RT` (analog triggers, hysteresis-gated)

Input mode auto-detects but can be forced via `Input.Mode`.

## Configuration

Generated on first run. Notable sections:

- `[Rowing]` - tempo gain, decay, speed cap, ZDO sync interval
- `[Rhythm]` - PERFECT/GOOD/SLOW/TOO FAST windows and bonuses, wrong-side penalty
- `[Streak]` - streak threshold, mastery min/max streak and factors
- `[CrewSync]` - bonus multipliers for 2 / 3 / 4+ rowers in phase
- `[Hud]` - HUD and metronome visibility, tick volume
- `[Input]` - keyboard keys, gamepad axes, mode override
- `[Debug]` - `VerboseLogs` (off by default; emits BepInEx Debug-level events when on)

## Build

Requires the Valheim and BepInEx assemblies in `..\libs\` (see the `.csproj` references). The post-build target copies the DLL + PDB to `BepInEx/plugins/` - update `<ValheimPlugins>` in the `.csproj` for your install path.

```
dotnet build -c Release
```

## License

See repository.
