# PCL 2.99.0.1 verification

Tool version: 1.0.0.4. Author: Codex. Date: 2026-10-26 (Asia/Tokyo).

## Inputs and review

- Official binary commit: `93ede5d9e638c99fcec853a9ba1eecd91ceee7f5` (`2.99.0.1 正式版`). The GitHub latest Release still reports 2.99.0.0. The maintainer now checks the official root ZIP's file-changing commit as well.
- Official source change: `e3d1cffed50dd2c7d57f5c0426c5b59ae92d28ce`.
- Input EXE SHA256: `0db34e19d9eeb6d496d20fabe786ec16bbd36ba6b0ccdc8d29cb7e909c114397`.
- Repaired EXE SHA256: `40d3f95100129fb61e190f10de4a9cf8bff4832f44611e3c1bdeaab11126609f`.
- Schema 2 restores the existing five download methods plus seven reviewed entry methods, using official 2.14.0.0 donor bodies. All 148 direct local dependency guards match 2.99.0.0. The UpdateMark initializer remains compatible with the existing reviewed literal replacement.
- `ModSecret.ServerSub` protects settings against remote notification updates. That protection remains byte-equivalent at symbolic IL level. Every other method outside the twelve-method allowlist and UpdateMark initializer, and every embedded resource, also remains equivalent.

## Verification

`scripts/verify.ps1 -EntryFixture <isolated official 2.99.0.1 EXE>` passed: 123 core/feed/entry checks, 44 bootstrap checks, 6 publisher checks, 13 maintainer checks for each official fixture, 42 pipeline tests and 16 official-download tests (257 outer checks). Each maintainer self-test additionally performs three ten-check preparations. Version and strict documentation gates passed. Both Python suites passed again after the pipeline was connected to the new fixture parameter.

The four self-contained packages built successfully. Each binary actually ran apply and restore against a separate new 2.99.0.1 copy, producing the same repaired hash and restoring the exact input hash:

| Helper | Execution environment |
| --- | --- |
| Windows ARM64 | Native Windows ARM64 |
| Windows x64 | Windows x64 emulation on ARM64 |
| Linux ARM64 | Native aarch64 Ubuntu 26.04 under WSL2 |
| Linux x64 | QEMU x86_64 with Ubuntu sysroot under ARM64 WSL2 |

The Linux tests verify the repair CLI, not native Linux execution of the WPF launcher. No native x64 computer was used.

In a new GUI test directory, actual PCL 2.99.0.1 installations completed:

1. Vanilla Minecraft 26.3: version JSON/JAR and installation completion recorded by PCL.
2. Online Modrinth Fabulously Optimized 15.0.0-beta.2 for 26.3: installation completed, Fabric version metadata and 46 mod JARs present.
3. Local `PCL-Repair-Local-Test.mrpack`: Minecraft 1.21.11 installation completed and `repair-test-marker.txt` from overrides present.

The real CLR auto-start test enabled automatic repair on a fresh 2.99.0.0 copy, replaced only its EXE with official 2.99.0.1, then launched PCL directly. The bootstrap handed off, repaired twelve methods, updated the verified hash, and reopened PCL successfully. This simulates the updater's EXE replacement; this turn did not initiate a live upstream self-update. The previous delivery directories were not modified.

Private local evidence: `Work/latest-check-649d8e9107b74bfaa497cef74e777301/` (`verify.log`, `publish.log`, `guard-audit/`, `gui/`, four architecture roundtrips, `auto-transition/`). Launcher copies and raw logs are excluded from public packages.

## Preservation and limits

Original ZIP and reference backup SHA256 both remain `6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b`. Neither was executed, unpacked or changed. New ready-to-use local copies are under `Artifacts/Windows-ARM64-29901/` and `Artifacts/Windows-x64-29901/`.

Old helpers need a one-time upgrade to 1.0.0.4 because signed recipes cannot expand their compiled method allowlist. Future structural or semantic changes still fail closed. Scheduled maintenance was already paused when inspected; its prompt was updated for the new scope while preserving that paused state. Current operating instructions remain owned by README.md.
