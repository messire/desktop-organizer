# Golden migration fixtures (ADR-024 D-9, ADR-025 §6 R-3)

These files are **immutable after merge**. They pin down the exact on-disk shape of every schema
version of the versioned JSON application-state document (`schemaVersion` 1, 2, 3, 4, 5, 6, 7), and the
exact result of migrating each of them up to the current schema version.

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
| `v5-full.json` | A realistic full document at schema v5 (additive `geometry.geometryMode`/`widthSpanUnits`/`heightSpanUnits`/`sizingMode` and `appearance.iconLayoutPolicy`, ADR-028/ADR-029, docs/PLANS/workspace-grid-migration.md), including one `GridAligned`/`AutoContent`/`FixedGap` panel, used as migration input. |
| `v6-full.json` | A realistic full document at schema v6 (`pages.items[{id,name?}]` replaces `pages.ids[]`, ADR-043, docs/PLANS/page-display-names.md), including an unnamed `"default"` page, a renamed ordinary page (`"work"` -> `"Renamed Work"`), and a GUID-id page with a Cyrillic name, with `currentPageId` pointing at the GUID page — used as migration input (`V6ToV7Migration` is an identity). |
| `v7-r0-full.json` | A realistic full document at the current schema v7, additive revision 0 (`"schemaVersion": 7`, `"schemaRevision": 0`; payload identical to `v6-full.json`) — used to prove migrating an up-to-date document is a no-op. **One `v7-rN-full.json` per additive revision `N` (0..`StateEnvelope.CurrentSchemaRevision`) must exist** — `SchemaContractTests.RevisionFixtures_ExistForEveryRevisionUpToCurrent` fails otherwise (ADR-024 Amendment "Additive revisions"). |
| `v7-unknown-fields.json` | A schema-v7 document as a hypothetical *newer* build of the same major would write it (`"schemaRevision": 99`): unknown keys (including `null` values and nested objects) on every DTO level — root, panel + geometry/appearance/content, pages section and page item, layout snapshot and its panels, system icons panel and its items — unknown members of every enum field (`Floating`, `Mica`, `Tiles`, …, `systemIconsPanel.iconSize`), and a system-icon item of an unknown `kind`. Drives the load → save round-trip tests. |
| `expected-current-from-v1.json` | `v1-full.json` migrated all the way to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v1-full, 7)`. |
| `expected-current-from-v2.json` | `v2-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v2-full, 7)`. |
| `expected-current-from-v3.json` | `v3-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v3-full, 7)`. |
| `expected-current-from-v4.json` | `v4-full.json` migrated to the current schema version — the expected output of `StateMigrationRunner.MigrateToCurrent(v4-full, 7)`. Every panel gets the schema-v5 additive fields with their safe defaults (`geometryMode: "LegacyFreeform"`, `sizingMode: "Manual"`, `appearance.iconLayoutPolicy: "LegacyStretch"`, no `widthSpanUnits`/`heightSpanUnits`) — existing `x`/`y`/`width`/`height` are untouched. |
| `expected-current-from-v5.json` | `v5-full.json` migrated to the current schema version (ADR-043) — the expected output of `StateMigrationRunner.MigrateToCurrent(v5-full, 7)`. Only `pages` changes shape (`ids: ["default","work"]` -> `items: [{"id":"default"},{"id":"work","name":"work"}]`); every panel/layoutSnapshot/systemIconsPanel field is untouched. |
| `expected-current-from-v6.json` | `v6-full.json` migrated to the current schema version (ADR-024 Amendment "Additive revisions") — the expected output of `StateMigrationRunner.MigrateToCurrent(v6-full, 7)`. The payload is identical (`V6ToV7Migration` is an identity migration); only `schemaVersion` changes. `schemaRevision` is deliberately absent: it is an envelope key written by `StateEnvelope.Wrap` when the store persists, never by a migration. |

`writtenAtUtc`, `appVersion` and (v7+) `schemaRevision` are carried through every migration completely unchanged (only
`schemaVersion` is ever rewritten by the migration chain — see `StateMigrationRunner`), so each
`expected-current-from-*.json` file has the exact same `writtenAtUtc`/`appVersion` values as its
source fixture. When schema v6 (ADR-043) shipped, `expected-current-from-v1.json` through
`expected-current-from-v4.json` were regenerated to the new target version (ADR-025 §6 R-3's named
exception) — their input fixtures (`v1-full.json` .. `v4-full.json`) were **not** touched.
When schema v7 shipped, `expected-current-from-v1.json` through `expected-current-from-v5.json` were regenerated
the same way (only the `schemaVersion` line changed) and `expected-current-from-v6.json` was added; the input fixtures
`v1-full.json` .. `v6-full.json` were **not** touched.
