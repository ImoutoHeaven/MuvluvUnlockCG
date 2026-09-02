# Development rules

## Runtime behavior

- Normal must remain stock behavior.
- LocalBypass may change only the selected row's presentation, entry, SceneFrame provider, and matching business mutations.
- Native dataSource, Blob, BestHTTP, Addressables, CDN, DirectCache, and media loading remain enabled.
- Production discovery is Master-driven. Unknown or ambiguous state fails open.
- Local sessions are scoped to one generation and cleared on replacement, completion, abandon, unload, or error.

## Evidence and checks

- Support every Harmony target and behavior change with exact local Cpp2IL or interop evidence kept outside Git.
- Test policy, source, and session behavior at public boundaries.
- Treat warnings as errors.
- Run checks in ephemeral Docker containers with game inputs mounted read-only.
- Use non-sensitive structural metadata in logs and fixtures.
- Distribute manual-install release artifacts.
