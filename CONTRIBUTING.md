# Development rules

## Runtime behavior

- Normal must remain stock behavior.
- LocalBypass may change only the selected row's presentation, entry, SceneFrame provider, and matching business mutations.
- Native dataSource, Blob, BestHTTP, Addressables, CDN, DirectCache, and media loading remain enabled.
- Production discovery is Master-driven. Unknown or ambiguous state fails open.
- Local sessions are scoped to one generation and cleared on replacement, completion, abandon, unload, or error.

## Evidence and checks

- Cite exact local Cpp2IL or interop evidence for every Harmony target and behavior change; do not commit that evidence.
- Test public policy, source, and session behavior; do not assert private call order.
- Treat warnings as errors.
- Run checks in ephemeral Docker containers with game inputs mounted read-only.
- Keep logs and fixtures free of credentials, URLs, account data, dialogue, and payloads.
- Build a manual-install release; never deploy from repository automation.
