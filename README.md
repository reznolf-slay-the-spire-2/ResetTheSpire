# Reset the Spire

- Adds a **Reset** button directly under **Resume** in the in-run pause menu. It is styled red, like
  **Give Up**, and clones the game's own button node so the art, hover/press animation and
  controller focus behavior match.
- Clicking it opens a short confirmation popup, then starts a fresh run with:
  - same character
  - same ascension level
  - same run modifiers (preserved for custom and daily runs)
  - same custom Act 1 choice (custom runs)
  - a newly generated seed (**standard/custom**), or the same day's seed (**daily**)
- The run starts again at **Act 1**. In standard/custom runs the act sequence, map, rooms,
  encounters, bosses and rewards are rerolled from the new seed, just like starting a run from
  character select. In daily runs the same daily act lineup is reused.
- Standard/custom attempts are not recorded as abandoned runs. **Daily** attempts are:
  the partial run is written to run history as abandoned (including its daily score) before the
  replacement daily starts, so retries cannot dodge the leaderboard.
- The Reset button dims together with the other pause-menu buttons during **Save and Quit**.

## Availability

The button is visible and enabled for **singleplayer** runs in **Standard**, **Custom** and
**Daily** mode, and for **multiplayer** hosts and clients.

Multiplayer requires every player to have the same version of this mod (`affects_gameplay: true`).

## Multiplayer

The host is authoritative:

1. A host or client presses **Reset** (a client's press sends a request to the host).
2. The host builds the new run configuration and sends it to every client.
3. Each client tears down its old run and acknowledges; the host waits for those acknowledgements.
4. The host tears down its own run, creates the new run state, and signals everyone to start.
5. All peers start the same run: same seed, players, characters, ascension, modifiers and acts.

The network session is kept alive across the reset (the game's normal run teardown would close it),
so players do not need to re-invite or rejoin. In standard/custom multiplayer runs the host rerolls
the act sequence from the new seed; in daily multiplayer runs the same daily seed and act lineup are
used, and each peer records its own partial attempt as abandoned first.

If a client fails to prepare or disconnects during the handshake, the host aborts the reset and the
session is closed rather than starting a run with a missing player.

## Implementation notes

- Code-only mod (`has_pck: false`), no BaseLib dependency.
- `NPauseMenu._Ready` postfix clones the game's own **Give Up** button, so the Reset button inherits
  the pause-menu artwork, red tint, hover/press animation and controller focus behavior.
- `NPauseMenu.Initialize` postfix controls visibility and rebuilds keyboard/gamepad focus neighbors,
  including when the button is hidden.
- Reset captures the run configuration, waits for any pending save, fades out, closes the pause
  menu, calls `RunManager.CleanUp()`, deletes the current run save, drops the old `NRun` scene, and
  calls `NGame.StartNewSingleplayerRun(...)` with a fresh `SeedHelper.GetRandomSeed()`.
- A confirmation popup (the game's own vertical popup scene) guards the destructive action.
- All patch bodies are wrapped in `try/catch`; failures are logged and never escape into game
  callbacks.

## Files

| Path | Purpose |
|---|---|
| `ResetTheSpireCode/PauseMenuReset.cs` | Pause-menu button, confirmation, singleplayer reset flow |
| `ResetTheSpireCode/MultiplayerReset.cs` | Host-authoritative multiplayer reset protocol and session-preserving teardown |
| `ResetTheSpireCode/ModImagePatch.cs` | Loads `mod_image.png` from disk for the modding screen |
| `ResetTheSpireCode/MainFile.cs` | Mod initializer / Harmony bootstrap |
| `ResetTheSpire.json` | Manifest |

## Workshop

Published as item **3828395701**:
https://steamcommunity.com/sharedfiles/filedetails/?id=3828395701

Workshop staging lives in `..\tools\ModUploader\workspace\ResetTheSpire` (keep its `mod_id.txt`).
Non-Steam release zip: `dist\ResetTheSpire-v1.1.0.zip`.

