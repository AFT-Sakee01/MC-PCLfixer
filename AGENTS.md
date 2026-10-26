# Project rules

Applies to PCL (Local Download Repair), version 1.0.0.4.

- Follow the user's global rules. Model shorthand: Codex.
- `PCL2DLA.zip` and `Backups/` are immutable reference material. Never execute, update, overwrite, or remove them.
- All launcher execution and installation tests use new copies under `Work/` or `Artifacts/`.
- Do not read or redistribute the original archive's account settings, launch scripts, logs, or shortcuts. Only its executable and explicitly required non-secret update setting may be inspected.
- Indexes are `Docs/Indexes/FEATURE_INDEX.jsonl` and `Docs/Interfaces/INTERFACE_INDEX.jsonl`.
- Current usage and architecture owner: `README.md`. Verification evidence belongs in `Docs/Reports/`; historical records belong in the append-only CHANGELOG.
- Upstream donor code is attributed to 龙腾猫跃 / Meloong-Git/PCL. This is an independent third-party patch, not an official PCL release.
- Preserve authentication, ownership checks, download integrity checks and sponsor features. Only the five documented download methods, the seven reviewed download entry methods in recipe schema 2, and the reviewed UpdateMark literal selecting full-package updates may change. Preserve the remote-settings blacklist in ModSecret.ServerSub. External startup loading is scoped to a launcher-specific .exe.config, with an explicit disable command.
- Unknown method structures must fail closed. No offset guessing or whole-file replacement with an older launcher.
- Never claim an architecture has been tested solely because it was compiled.
- Public recipe catalogs must be signed by the locally pinned publisher key. Keep signing keys in Windows CurrentUser CNG, never in this repository, packages, logs, or documentation. The publisher's own key name is not secret.
- Public releases contain helper binaries, source, notices and signed recipes only; never include launcher copies, test fixtures, original ZIP, backups, private settings or logs.
