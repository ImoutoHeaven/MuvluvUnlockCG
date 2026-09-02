# Repository instructions

- Read `README.md`, `CONTEXT.md`, and `CONTRIBUTING.md` before changing code.
- Treat game installations, the G4 distribution, licenses, and game caches as read-only.
- Run restore, build, test, decompile, and review commands with `docker run --rm` and read-only input mounts.
- The licensed G4 exporter is the only host-runtime exception; it runs under the current Windows user and writes only to a new repository artifact directory.
- Reuse local decompile evidence from ignored `evidence/` and keep it private.
- Tie every runtime hook and behavior fix to exact local decompile evidence.
- Derive production Episode, Character, Memory, Scene, and asset identifiers from runtime Master data.
- Preserve native behavior when identity or session evidence is incomplete.
- Restrict logs to non-sensitive structural and control-flow metadata.
- Write build artifacts only to repository output directories for manual installation.
