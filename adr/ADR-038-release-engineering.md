# ADR-038: Release Engineering — Installer, CI, and Manual Update Check

**Статус:** Accepted (2026-09-27) — Amended 2026-09-27 (release polish 1.0: application/installer metadata) —
Amended 2026-09-28 (Company = khagendev, user.config migration; supersedes the 2026-09-27 amendment's
"Company pinned to DesktopOrganizer.UI" decision). **Open (ADR-audit WP-E):** CI and the update check are inert while
the repository has no remote and no `v*` tags — see `docs/ROADMAP.md`, "Решения, ожидающие пользователя", item 4. — Amended 2026-10-10 (user decision, ROADMAP item 4: RC convention extended with an
annotated `vX.Y.Z` tag per squash commit; CI also runs on `rc/**`; retro tags v0.5.1…v0.8.0 created locally; the remote is still
to be added by the user — the "Open" note above is therefore narrowed to "no remote yet").

## Context

Before this package the project had no CI, no root-level `README`/`LICENSE`/`CHANGELOG`, an installer whose
publisher/support URLs were placeholders, no code-signing story, and no way for an installed copy to learn
a newer version exists short of the user manually checking GitHub. None of this blocks the application from
working — it blocks it from being *releasable* as a 1.0: a stranger downloading an unsigned installer with
placeholder URLs, from a repository with no license file, has no way to evaluate trust or find support.
This package (release-hardening plan §7, G1 + G2) closes that gap without introducing any new persisted
state, telemetry, or automatic network activity — the project's "no telemetry" claim (README) had to
remain true after this package, not just before it.

## Decision

### G1 — installer, CI, and repository documents

**Installer publisher/support links.** `installer/DesktopOrganizer.iss`: `AppPublisherURL`/`AppSupportURL`
now point at `https://github.com/messire` (`MyAppURL`) instead of a placeholder; `MyAppPublisher` stays
`"khagen"` unchanged — the orchestrator's Q4 decision (`release-hardening-1.0.md` §10) was variant B (MIT
license under "messire" as copyright holder, installer publisher name left as-is), not variant A (renaming
the publisher too).

**Optional code signing, never blocking an unsigned build.** `installer/build.ps1` signs the publish
output's `DesktopOrganizer*.exe/.dll` via `signtool` and then hands Inno Setup its own `SignTool=dosign`
directive (guarded by `#ifdef SignEnabled` in the `.iss`, enabling `SignedUninstaller=yes` too) — but only
when `DESKTOPORGANIZER_SIGN_CERT` is set; absent it, the script prints an explicit "unsigned build" notice
and produces exactly the same unsigned output as before this package. `DESKTOPORGANIZER_SIGN_CERT` accepts
either a `.pfx` file path or a certificate-store SHA1 thumbprint. A review finding (M5/L8) hardened the
design further: **the Inno Setup sign command line always signs by certificate-store thumbprint, never by
`/f <pfx> /p <password>`** — ISCC echoes its own `SignTool=` command line into its compile log, so a
plaintext password there would be a real leak; a `.pfx` is imported into `Cert:\CurrentUser\My` first (the
script removes it again in a `finally` block, and CI additionally removes it in its own `always()` step as
a second, independent safety net). The password is masked in this script's own console output and never
appears in any log ISCC itself produces. `-p:ContinuousIntegrationBuild=true` was added to the publish step
(deterministic `/_/`-style paths in stack traces — supports the crash-log privacy scrubbing ADR-033 relies
on).

**Root `README.md`/`LICENSE`/`CHANGELOG.md`.** `README.md` gained an English section and a Russian section
(what the app is, install/uninstall, feature list, where data/logs live, `--diag`/`--restore-state-backup`,
known limitations, license/support link). `LICENSE` is MIT, copyright "messire" (Q4 decision, above).
`CHANGELOG.md` follows Keep a Changelog, built from the repository's own squash-commit history
(`vX.Y.Z` tags/commit bodies on `master`) — versions before `0.1.0` are explicitly not tracked (no tagged
history point exists before it).

**CI (`​.github/workflows/ci.yml`), Windows-only by necessity.** The app targets `net8.0-windows`/WPF and
its own tests exercise real Win32 P/Invoke (`INativeApi`) — there is no meaningful non-Windows runner for
this repository. `build-and-test` restores, builds Release with `-p:TreatWarningsAsErrors=true` (the
project-wide "0 warnings" gate from `CLAUDE.md` enforced mechanically, not just by convention), and runs
`dotnet test --filter "Category!=RequiresDesktop"` — a forward-looking trait filter (no test currently
carries it; it exists so a future test that genuinely needs a live interactive desktop can opt out without
touching the workflow again). A second job, gated on `refs/tags/v*`, builds the installer (optionally
importing a signing certificate from repository secrets, all three of which are optional — CI never fails
just because signing secrets are absent) and attaches `Setup.exe` to the tag's GitHub Release.
Least-privilege `permissions: contents: read` at the workflow level, `contents: write` only on the one job
that needs it; the `installer` tool version and the `action-gh-release` action are both **pinned**
(`innosetup --version=6.7.1`, `softprops/action-gh-release@<commit-sha> # v2.6.2`) rather than tracking a
floating tag — an upstream release being yanked or a third-party action's tag being moved must not silently
change what CI builds with.

### G2 — manual update check and About links

**No automatic network activity, no persisted state.** There is no "last checked" timestamp, no
autocheck-at-startup — the only way `IUpdateCheckService.CheckForUpdatesAsync` ever runs is the user
clicking "Check for updates" in the About section. The result lives only in `AboutViewModel`'s own
in-memory state for the session.

**`SemVersion`** (`Core/Versioning`) — a SemVer 2.0 parser/comparer (strips a leading `v`, understands
`+build`/`-prerelease`, prerelease sorts below release) used both to compare the running build's own
`AppVersionInfo.Display` against the latest tag and, implicitly, to validate that tag is well-formed at all
(an unparsable current-build version — a dev/local build — reports `Unexpected` rather than guessing).

**`GitHubReleaseUpdateChecker`** (`Services/Updates`) — one GET against
`https://api.github.com/repos/messire/DesktopOrganizer/releases/latest`
(`UpdateEndpoints.ApiLatestReleaseUrl`; `UpdateEndpoints.Repo`/`ReleasesUrl`/`IssuesUrl`/`ProjectPageUrl`
are the complete, compile-time-constant set of external endpoints this application will ever contact or
open — there is no settings surface to point it elsewhere). Owns exactly one `HttpClient` for the DI
container's own lifetime, 10-second timeout, `User-Agent: DesktopOrganizer/<AppVersionInfo.Display>`,
`Accept: application/vnd.github+json`, a 256 KB response-body cap (anything larger is treated as an invalid
response rather than buffered in full). `404` → `NoReleases` (the repository has no releases yet, or isn't
public); `403`/`429` → `Failed(RateLimited)`; any other non-success or a malformed/oversized/unparsable body
→ `Failed` with a specific `UpdateCheckFailureKind`. **Never throws** — every failure mode is caught inside
the checker itself and turned into a result value.

**`html_url` is validated structurally, not by a naive prefix check (M4 finding).**
`UpdateEndpoints.TryGetTrustedReleaseUrl` parses the release API response's own `html_url` via `Uri` (scheme
must be `https`, host must be exactly `github.com`, path must start with `/messire/DesktopOrganizer/`) —
this closes a look-alike-domain/redirect class of trick a bare `StartsWith("https://github.com/messire/")`
string check would be more fragile against; an untrusted or absent `html_url` falls back to the fixed
`ReleasesUrl` instead of being opened at all.

**`IExternalLinkLauncher`** (`UI/Services`) — every external open goes through `OpenUrl`/`OpenFolder`, never
a raw `Process.Start` at each call site. `OpenUrl` (M4 finding) only launches a well-formed, absolute
`https://` URL via `ShellExecute` — a `javascript:`/`file:`/bare-`http://`/malformed string is logged and
refused, never handed to the shell. Every failure (no default browser, a declined UAC prompt, a deleted
folder) is logged and swallowed — these are always convenience actions, never on a critical path.

**`AboutViewModel`** (`UI/ViewModels`) exposes `CheckForUpdatesCommand` (async, `IsChecking` disables the
button while in flight), `OpenReleaseCommand` (opens the specific release URL from the last
`UpdateAvailable` result), `OpenProjectPageCommand`/`ReportProblemCommand` (the two fixed
`UpdateEndpoints` links), and `OpenLogsFolderCommand` (creates the logs folder if missing, then opens it) —
a small naming deviation from the original plan sketch (`OpenReleasesCommand` → split into
`OpenReleaseCommand` + `OpenProjectPageCommand`, since the About section links to both the author's project
page and, only when relevant, the specific release). Status text is fully localized (`Settings_About_
Update_*` keys) and re-resolves on a hot language switch via `ILocalizationService.CultureChanged`, matching
the convention `MainWindowViewModel.LanguageOptions` already established (ADR-032).

## Consequences

Positive:
- The project is now releasable as a stranger-facing 1.0: a license, a README that explains what the
  installer does and where its data lives, and a CI pipeline that gates every merge on the project's own
  "0 errors, 0 warnings" standard.
- Signing is fully optional and fails safe — CI, a local dev machine, and a future maintainer with a real
  certificate all produce a working installer either way, with no code branch that requires a certificate
  to build at all.
- The update check adds the project's very first outbound network call, scoped as narrowly as the plan
  could make it: one constant URL, no autocheck, no persisted history, structurally-validated redirect
  target, hard body-size cap, and a launcher that refuses anything but an `https://` URL.

Negative:
- `MyAppPublisher` staying "khagen" while `LICENSE` says "messire" is an intentional but visible asymmetry
  a future maintainer should not "fix" without re-confirming the Q4 decision this ADR records. **Superseded
  by the 2026-09-27 Amendment below**: `LICENSE` now says "khagen" too — see the amendment's own "known
  asymmetry" statement for what remains open (the GitHub repository owner, "messire", left unchanged).
- GitHub's unauthenticated rate limit (60 requests/hour per IP) is shared across every DesktopOrganizer
  install checking from behind the same NAT/proxy — acceptable for a manual button, not for anything more
  frequent; there is deliberately no autocheck specifically because of this.
- Pinning tool versions/action SHAs (Inno Setup, `action-gh-release`) means a future maintainer must
  manually bump them — a small, recurring maintenance cost traded for supply-chain predictability.

## Amendment (2026-09-27, release polish 1.0 — application/installer metadata)

A second, narrower pass over the same "releasable as a stranger-facing 1.0" goal: Task Manager showed the
raw assembly name, the installer's own version resource carried placeholder-shaped metadata, and `LICENSE`
still said "messire" while every other author-facing surface (installer `AppCopyright`, `Directory.Build.
props`) already said "khagen" — an internal inconsistency worth closing in the same release, not a new
decision area.

**`Product`/`AssemblyTitle` split, not a single "app name".** `Directory.Build.props` sets
`<Product>DesktopOrganizer</Product>` solution-wide (feeds the Win32 `VS_FIXEDFILEINFO` "ProductName"
field — what Explorer's file-Properties "Product name" shows). Task Manager's own "Name" column and
Explorer's "File description" field read `AssemblyTitle`, **not** `Product` — a fact that only became load-
bearing once someone actually checked what Task Manager reads. `DesktopOrganizer.UI.csproj` therefore sets
`<AssemblyTitle>DesktopOrganizer</AssemblyTitle>` directly on the UI exe (its `AssemblyName` stays
`DesktopOrganizer.UI` — renaming *that* would move the per-user `Settings.settings` `user.config` path under
a new `%LocalAppData%\...\<AssemblyVersion>\` folder and reset every user's settings, the exact class of
regression the frozen-persistence-contour rule in `CLAUDE.md` exists to prevent; both the installer and
`SingleInstanceNaming` also reference `DesktopOrganizer.UI.exe` by its literal file name).

**`Company` is pinned per-project, not inherited, on the UI exe — because it determines the `user.config`
path.** `Directory.Build.props` sets `<Company>khagendev</Company>` solution-wide (feeds the installer's own
`VersionInfoCompany` and every other project's assembly metadata). `DesktopOrganizer.UI.csproj` overrides it
to the literal `<Company>DesktopOrganizer.UI</Company>` — **not** because the UI exe needs a different
company name for its own sake, but because `System.Configuration` derives this exe's per-user
`Settings.settings` `user.config` folder from `AssemblyCompanyAttribute`:
`%LocalAppData%\<Company>\<exe>_Url_<hash>\<AssemblyVersion>\user.config`. Changing this value has the exact
same "every user's settings silently reset on upgrade" effect as renaming `AssemblyName` or moving
`AssemblyVersion` (ADR-024's frozen contour) — it must never inherit `Directory.Build.props`'s `<Company>`
value again, even if that value changes for an unrelated reason. `AssemblyCompanyContractTests`
(`tests/DesktopOrganizer.Tests/Unit/Installer/`) locks this literal down mechanically, not just by comment.

**Copyright/Authors/LICENSE holder — unified on "khagen".** `Directory.Build.props`'s
`<Copyright>Copyright (c) 2024-2026 khagen</Copyright>`/`<Authors>khagen</Authors>`, the installer's
`AppCopyright=Copyright (c) 2024-2026 khagen`, and `LICENSE`'s copyright line now all agree — closing the
inconsistency this amendment started from (`LICENSE` previously said "messire", every other surface already
said "khagen"). `MyAppPublisher`/`VersionInfoCompany` in the installer stay `"khagen"`/`"khagendev"`
respectively (installer-only fields, unaffected by this rename).

**Installer: English-first, `MinVersion=10.0`, and a trimmed publish payload.** `[Languages]` now lists
`english` before `russian` — Inno Setup falls back to the *first* `[Languages]` entry for any UI language it
does not recognize, so the fallback had to be English, not Russian. `[Setup]` gained
`MinVersion=10.0` — refusing to run Setup at all on anything older than Windows 10, matching the app's actual
`net8.0-windows`/WPF requirement, instead of failing later at first launch with a less legible error.
`[Setup]`'s own `VersionInfoCompany`/`AppCopyright` (Setup.exe/uninstall.exe's *own* Win32 version resource,
separate from the app exe's, which already gets its values from `Directory.Build.props`) were filled in for
the same reason the app exe's were. `[Files]` now excludes `*.pdb`/`*.xml` from the publish payload it packs
(`Excludes: "*.pdb,*.xml"`) — neither debug symbols nor `GenerateDocumentationFile=true`'s XML-doc comments
are needed at runtime; both were dead weight the installer previously shipped unconditionally.

**GitHub URLs unchanged, decision deferred.** `MyAppURL`/`UpdateEndpoints`'s repository owner segment (both
`https://github.com/messire`/`.../messire/DesktopOrganizer`) were **not** touched by this pass — the
"asymmetry" this ADR's original Consequences section already flagged (`MyAppPublisher` "khagen" vs. GitHub
owner "messire") remains open, now alongside the *closed* LICENSE-holder inconsistency above. Renaming or
migrating the GitHub organization/repository is a decision only the user can make (it affects live release
URLs, issue tracker links, and the update-check endpoint) — left exactly as ADR-038 originally recorded it,
pending that explicit decision.

**Updated "known asymmetry" statement.** The Negative-consequences bullet below about `MyAppPublisher` vs.
`LICENSE` is superseded by this amendment (`LICENSE` now says "khagen", matching `MyAppPublisher`) — the
asymmetry that remains, going forward, is narrower: **`MyAppPublisher`/`LICENSE`/`Copyright` all say
"khagen"; the GitHub repository owner and every URL derived from it still say "messire"** — a future
maintainer should not "fix" that mismatch either without the same explicit user decision this amendment
itself deferred.

## Amendment 2026-09-28 (Company = khagendev, user.config migration)

A third pass, same day as ADR-042's own amendment, closes the "Company pinned to `DesktopOrganizer.UI`"
decision the 2026-09-27 amendment above recorded — **with the user's explicit approval, given in this
task, to change it** — and resolves the GitHub-URL open question the same amendment deferred.

**`Company` no longer pinned per-project.** `DesktopOrganizer.UI.csproj`'s `<Company>DesktopOrganizer.UI</
Company>` override is removed; the UI exe now inherits `Directory.Build.props`'s solution-wide
`<Company>khagendev</Company>`, same as every other project (`AssemblyCompanyContractTests` updated to pin
`"khagendev"` on both the UI and Services assemblies instead of asserting they differ). This **supersedes**
the previous amendment's decision to pin the literal `"DesktopOrganizer.UI"` specifically to keep the
`user.config` path stable — the user judged the metadata consistency (one `Company` value everywhere) worth
the one-time migration cost below, and approved it explicitly rather than this being inferred.

**This is the one sanctioned exception to the ADR-024 frozen persistence contour.** `CLAUDE.md`'s frozen-
settings-contour rule exists precisely to stop an agent from moving/renaming the `user.config`/
`application-state.json` storage roots without the user's own explicit "yes" in the task — this change does
exactly that (moves where `Settings.settings`'s `user.config` lives), but only because the user gave that
yes here. Nothing else in the frozen contour moves: `schemaVersion` stays 5, no `Migrations/V*To*` step is
added, `IAppSettings`' keys/defaults are untouched, `application-state.json`'s format/location
(`%LOCALAPPDATA%\DesktopOrganizer\State\`) does not change — only the unrelated, always-framework-managed
`.NET` `user.config` folder moves, and only because `AssemblyCompanyAttribute` is what `System.Configuration`
keys that folder on.

**One-time startup migration, not a silent relocation.** `UserConfigFolderMigration`
(`src/DesktopOrganizer.UI/Lifecycle/UserConfigFolderMigration.cs`, internal), run at the very top of
`Program.Main` (before `TryReadLanguagePreference`, the earliest `Properties.Settings.Default` read, but after
the headless `--restore-panel-items` path has already returned — that path never touches `user.config`).
Algorithm (hardened after code review, 2026-09-28):

1. Fast path: if `%LocalAppData%\khagendev\migrated-from-DesktopOrganizer.UI.txt` exists, return — the
   marker is the **only** "done" signal.
2. Resolve **this exe's own** evidence folder name (`DesktopOrganizer.UI_Url_<hash>`) from
   `ConfigurationManager.OpenExeConfiguration(PerUserRoamingAndLocal).FilePath` (path computation only, no
   settings load). If its company segment is not `khagendev` (dev build overriding `Company`), skip. Sibling
   evidence folders of other exe locations under the old root are never touched.
3. Take the named mutex `Local\DesktopOrganizer.UserConfigFolderMigration` (≤10 s; timeout → skip this launch,
   no marker, so the next launch retries). Two simultaneous first starts therefore cannot race.
4. If the old folder `%LocalAppData%\DesktopOrganizer.UI\<evidence>` does not exist → no-op (fresh machine).
5. If the final folder already exists under the new root but there is no marker (a resumed partial run),
   copy only the missing files (`overwrite:false`). Otherwise copy into a staging folder
   `.migrating-<evidence>` (never shaped like a real evidence folder), verify every file length, delete a
   mismatched copy, and only on zero failures `Directory.Move` the staging folder onto the final name; any
   failure discards the staging folder, so a half-copied folder is never exposed.
6. Write the marker only when everything succeeded; otherwise leave none so the next launch retries.
7. Reparse points are skipped; the old `DesktopOrganizer.UI` tree is **never deleted** (de facto backup).
8. Outcomes are appended to `migration.log` under the new root (AppLog is still a no-op this early) and a
   one-line summary is logged to `AppLog` right after `AppLog.Initialize`. `Run()` never throws — a failure
   degrades to "starts with default settings for this session and retries next start," never a crashed startup.

**GitHub-URL open question — resolved, no change.** The previous amendment's "GitHub URLs unchanged,
decision deferred" paragraph is closed: the user has confirmed the GitHub account/repository owner stays
`"messire"`. `MyAppURL`/`UpdateEndpoints`'s `.../messire/DesktopOrganizer` segment is intentionally
unchanged by this amendment (and was never in scope for it — only `Company`/`user.config` were). The
resulting, now-permanent asymmetry — `MyAppPublisher`/`LICENSE`/`Copyright`/`Company` all say "khagen"/
"khagendev"; the GitHub repository owner and every URL derived from it say "messire" — is no longer an open
question a future maintainer should revisit; it is the settled state.

## Manual verify (Win10/Win11)

Installer's "Programs and Features" entry shows the publisher/support links; SmartScreen behavior with and
without a real signing certificate; "Check for updates" online and offline (network failure produces a
readable, non-crashing message); a hot language switch while a status message is shown; every link opens in
the system default browser; CI green on a clean clone including the tag-triggered installer job (manual —
requires a real push, not reproducible from this environment). **Amendment 2026-09-28:** on a machine with an
existing `%LocalAppData%\DesktopOrganizer.UI\` folder from a prior install, first launch after upgrading
copies it to `%LocalAppData%\khagendev\` and the app starts with the previous language/one-time-hint
preferences intact (not reset to defaults); the old folder is still present afterwards.

## Related

- ADR-033 (Crash Safety and App Log) — `AppVersionInfo`/`ContinuousIntegrationBuild=true` support the same
  crash-report privacy/determinism goals that ADR relies on.
- ADR-032 (UI Localization) — the About section's update-check status text follows the same hot-switch
  convention this ADR's `AboutViewModel` reuses.
- `docs/PLANS/release-hardening-1.0.md` §7 (packages G1/G2).

## Amendments (сводка)

| Date | Source | What changed | Which sections/decisions no longer apply |
|---|---|---|---|
| 2026-09-27 | release polish 1.0 | `Product`/`AssemblyTitle` for Task Manager; installer English-first + `MinVersion=10.0`, `.pdb`/`.xml` excluded; Copyright/`LICENSE` holder unified to "khagen" | "`LICENSE` is MIT, copyright "messire"" (Q4 decision) |
| 2026-09-28 | user approval in task | `Company` = `khagendev` (inherited); one-time `UserConfigFolderMigration` of `user.config`; GitHub URL "messire" confirmed, question closed | The 2026-09-27 amendment's "`Company` pinned to `DesktopOrganizer.UI`" |
| 2026-10-09 | ADR-audit WP-E (clarification, no behavior change) | **What "pinned" means in `ci.yml` (verified):** only the Inno Setup tool version (`--version=6.7.1`) and `softprops/action-gh-release` (commit SHA, `# v2.6.2`) are pinned; `actions/checkout@v4`, `actions/setup-dotnet@v4` and `actions/upload-artifact@v4` track a major tag. The README row previously said "pinned actions/tool versions" (overstated) and has been aligned with this text | — |
| 2026-10-10 | user decision (ROADMAP item 4, commit `b796feb`) | RC convention: annotated tag `vX.Y.Z` on each rc squash commit; CI on `rc/**`; retro tags v0.5.1…v0.8.0 (local); remote — user action | "CI and update check are inert while there are no `v*` tags" (tags exist locally; remote still pending) |

## Amendment 2026-10-10 (user decision): release tags and CI on `rc/**`

*(Раздел на русском, как и другие поздние amendments; остальной документ — на языке оригинала.)*

Решение пользователя 2026-10-10 по п. 4 «Решений, ожидающих пользователя» (`docs/ROADMAP.md`):

- **Конвенция RC дополнена тегом.** Каждая rc-ветка `rc/X.Y.Z` по-прежнему заканчивается одним squash-коммитом `vX.Y.Z`
  (тело = сообщения feature-коммитов, bump `Version`); теперь на этот коммит ставится **annotated-тег `vX.Y.Z`**. Тег — то,
  что запускает tag-triggered job сборки инсталлятора (`ci.yml`, `if: startsWith(github.ref, 'refs/tags/v')`) и на что
  указывают ссылки `[X.Y.Z]` внизу `CHANGELOG.md`; `GitHubReleaseUpdateChecker` читает `tag_name` последнего GitHub Release — а Release создаётся этим job'ом из тега.
- **CI запускается и на ветках `rc/**`.** `on.push.branches` в `.github/workflows/ci.yml` — `[ master, 'rc/**' ]` (раньше
  только `master`), `tags: [ 'v*' ]` и `pull_request` на `master` без изменений: каждый push в rc-ветку получает гейт
  «0 errors / 0 warnings» до того, как squash-коммит получит тег.
- **Ретро-теги проставлены локально:** `v0.5.1`, `v0.6.0`, `v0.6.1`, `v0.7.0`, `v0.8.0` (сверено по `.git/refs/tags`) — на
  соответствующие squash-коммиты rc-веток.
- **Remote добавляет пользователь** (GitHub-владелец `messire`, см. Amendment 2026-09-28). Пока `origin` нет, CI, tag-job
  и update-check по-прежнему не могут сработать на практике: решение закрыто **кроме remote** и первого push тегов.

Поведение кода, `IAppSettings`, persistence не менялись (только `ci.yml` и документация процесса).

**State.** The workflow (`.github/workflows/ci.yml`) exists and, since 2026-10-10, is aligned with the RC-tag convention
(CI on `rc/**`, release job on `v*` tags); local tags v0.5.1…v0.8.0 exist. The repository still has no `origin` remote
(user action pending), so the CI jobs, the tag-triggered installer job and the GitHub-based update check cannot run in
practice until the remote is added and the tags are pushed. `docs/ROADMAP.md`, "Решения, ожидающие пользователя", item 4.
