# Repository instructions

- Read `README.md`, `CONTEXT.md`, and `CONTRIBUTING.md` before changing code.
- Treat game installations, the G4 distribution, licenses, and game caches as read-only.
- Run restore, build, test, decompile, and review commands with `docker run --rm` and read-only input mounts.
- The licensed G4 exporter is the only host-runtime exception; it runs under the current Windows user and writes only to a new repository artifact directory.
- Local decompile evidence may exist under ignored `evidence/`. Reuse it when present; never publish it.
- Tie every runtime hook and behavior fix to exact local decompile evidence.
- Derive content identifiers from runtime Master data. Do not hard-code production Episode, Character, Memory, Scene, or asset IDs.
- Preserve native behavior when identity or session evidence is incomplete.
- Never log credentials, URLs, headers, account data, dialogue, or Scene JSON.
- Produce manual-install artifacts only. Do not write builds into the game.
