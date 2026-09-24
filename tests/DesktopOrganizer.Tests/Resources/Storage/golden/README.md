# Golden migration fixtures (ADR-024 D-9, ADR-025 §6 R-3)

These files are **immutable after merge**. They pin down the exact on-disk shape of every schema
version of the versioned JSON application-state document (`schemaVersion` 1, 2, 3, 4, 5), and the exact
result of migrating each of them up to the current schema version.

**Do not edit an existing file here to make a failing test pass.** If a migration test fails
against one of these fixtures, the bug is almost always in the migration, not the fixture — a
golden file that gets silently "fixed" to match new migration output stops being a golden test and
starts hiding regressions. If the *target* schema genuinely needs to change (e.g. a new schema
version is introduced), add new fixtures for the new version rather than mutating these.

**The one exception, spelled out explicitly (ADR-025 §6 R-3):** `expected-current-from-*.json` files
are tied to "current" by definition, and "current" moves every time `CurrentSchemaVersion` does. When
a new schema version ships, every existing `expected-current-from-*.json` is **regenerated** to the
new target version — that is not an edit-to-make-a-test-pass, it is these files doing their job. The
**input** fixtures (`v1-full.json`, `v2-full.json`, `v3-full.json`, `v4-full.json`, …) are the ones
that never change once merged; a new schema version adds one new `vN-full.json` input and one new
`expected-current-from-v(N-1).json`, and regenerates every `expected-current-from-*.json` that isn't
brand new.

| File | What it represents |
|---|---|
| `v1-full.json` | A realistic full document at schema v1 (literal transcription of the four legacy `user.config` writers — ADR-024 D-9), used as migration input. |
| `v2-full.json` | A realistic full document at schema v2 (unified camelCase dialect, unpacked layout snapshot panels), used as migration input. |
| `v3-full.json` | A realistic full document at schema v3 (`geometry`/`appearance`/`content` grouping), used as migration input. |
| `v4-full.json` | A realistic full document at schema v4 (`appearance.accentColor`/`surfaceOpacity`/`surfaceStyle` replacing `backgroundColor`/`backgroundOpacity`, ADR-025 D-5), used as migration input. |
| `v5-full.json` | A realistic full document already at schema v5 (current — additive `geometry.geometryMode`/`widthSpanUnits`/`heightSpanUnits`/`sizingMode` and `appearance.iconLayoutPolicy`, ADR-028/ADR-029, docs/PLANS/workspace-grid-migration.md), including one `GridAligned`/`AutoContent`/`FixedGap` panel, used to prove migrating an up-to-date document is a no-op. |
| `expected-current-from-v1.json` | `v1-full.json` migrated all the way to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v1-full, 5)`. |
| `expected-current-from-v2.json` | `v2-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v2-full, 5)`. |
| `expected-current-from-v3.json` | `v3-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v3-full, 5)`. |
| `expected-current-from-v4.json` | `v4-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v4-full, 5)`. Every panel gets the schema-v5 additive fields with their safe defaults (`geometryMode: "LegacyFreeform"`, `sizingMode: "Manual"`, `appearance.iconLayoutPolicy: "LegacyStretch"`, no `widthSpanUnits`/`heightSpanUnits`) — existing `x`/`y`/`width`/`height` are untouched. |

`writtenAtUtc` and `appVersion` are carried through every migration completely unchanged (only
`schemaVersion` is ever rewritten by the migration chain — see `StateMigrationRunner`), so each
`expected-current-from-*.json` file has the exact same `writtenAtUtc`/`appVersion` values as its
source fixture.
