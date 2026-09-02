# MuvluvUnlockCG

BepInEx IL2CPP plugin for local playback of otherwise blocked Muv-Luv Girls Garden X episodes.

The plugin leaves account ownership, affection, rewards, progress, and server-side unlocks unchanged. Episodes allowed by the game use the untouched Normal route.

## Routes

| Episode | Normal | LocalBypass |
| --- | --- | --- |
| Character | Character owned and affection requirement met | Unowned or affection below requirement, with every expected SceneFrame available |
| Memory | Original cell is viewable | Released Memory outside the account's holdings, with every expected SceneFrame available |
| Main | Original row is viewable | Hidden row with a complete runtime Episode→Scene relation and every expected SceneFrame available |
| Event | Original row is viewable | Hidden row with a complete runtime Chapter→Episode→Scene relation |

Character, Memory, and Main remain locked when a required SceneFrame is unavailable. Event may continue through the game's native SceneFrame download after both plugin sources miss.

LocalBypass uses the native Scenario player and media loaders. It suppresses only matching read, reward/progress, branch-submission, and tracking calls. Incomplete or ambiguous identity keeps native behavior.

## SceneFrame sources

LocalBypass checks:

1. The HTTPS base in `[SceneFrames] RemoteBaseUrl`.
2. `BepInEx/plugins/MuvluvUnlockCG.SceneFrames/scene/<SceneId>/scene.json`.

The default is:

```text
https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/
```

The default applies to newly generated configs; existing values are preserved. The setting may be replaced or cleared for local-only behavior. A remote base must expose:

```text
manifest.json
scene/<numeric SceneId>/scene.json
```

The plugin validates manifest format, ID, byte count, SHA-256, document ID, and `muvluvFrame` schema. Accepted data is cached in memory only.

## Install

Installation is manual: build against the installed game's BepInEx/interop assemblies, then copy `MuvluvUnlockCG.dll` and `MuvluvUnlockCG.Core.dll` to `BepInEx/plugins/`.

## Reference

- [Domain glossary](CONTEXT.md)
- [G4 export tutorial](docs/g4-scene-export-tutorial.md)
- [Development rules](CONTRIBUTING.md)
