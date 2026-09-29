# ADR-039: Sized Shell Icons via IShellItemImageFactory

Status: Accepted (2026-09-27) — Amended 2026-09-30 ([ADR-046](ADR-046-background-icon-loading.md): the
"Known follow-up" below is closed — every `GetIcon` call now runs on a dedicated background STA worker
thread, not the UI thread; the three-stage fallback, the `(path, bucket)` cache, and the UNC/offline skip
described in this ADR are unchanged)

## Context

Every panel item icon went through `SHGetFileInfo` with `SHGFI_USEFILEATTRIBUTES | SHGFI_SHELLICONSIZE`
(`IconProvider.IconFlags`) — a flag combination that resolves the icon purely from the file's *attributes*
and *extension*, never opening the item itself. Two user-visible defects followed directly from that
choice: (1) `SHGFI_USEFILEATTRIBUTES` cannot resolve a `.lnk`'s own target icon or a folder's custom icon —
every shortcut and every custom-icon folder showed the generic `.exe`/folder type icon instead of its real
one; (2) `SHGFI_SHELLICONSIZE` caps the returned bitmap at the classic 32 px "large icon" size regardless of
the panel's actual `IconSize` (up to 96 DIP, more at HiDPI) — every icon above 32 px was a blurry upscale.
Fences shows real, per-item icons at native resolution; this was a visible parity gap, not a cosmetic nit.

The fix requires actually *opening* each item through the shell (`IShellItemImageFactory`) to get its real,
per-item bitmap at an arbitrary size — which is unavoidably more expensive and more failure-prone (COM,
network shares, cloud placeholders) than the pure-attribute lookup it replaces. The attribute-only path is
kept, not deleted: it is the correct (and only) resolution for a path that no longer exists, and a safe
final fallback when the expensive stages fail.

## Decision

### Three-stage fallback chain for an existing item, unchanged final stage for a missing one

`IconProvider.GetIcon(string, int)` tries, in order, each guarded by `try`/`catch` and degrading to the
next stage on any failure (never surfacing an exception to the caller):

1. **`IShellItemImageFactory::GetImage(SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)`** at the requested (snapped)
   pixel size — the item's own icon exactly as Explorer resolves it: shortcut target, embedded `.exe` icon,
   custom folder icon. No shortcut-arrow overlay is drawn (matches Fences, which shows none either).
2. **`SHGetFileInfo(SHGFI_SYSICONINDEX)`** — deliberately *without* `SHGFI_USEFILEATTRIBUTES`, so the shell
   still opens the item — plus `SHGetImageList`/`IImageList::GetIcon` against the system image list closest
   to the requested size (`IconHandler.SystemImageListFor`: 16→`SHIL_SMALL`, 32→`SHIL_LARGE`,
   ≤96→`SHIL_EXTRALARGE`, above→`SHIL_JUMBO`). Still the item's own icon, just capped to the image lists'
   fixed native sizes.
3. **The pre-1.0 `SHGFI_USEFILEATTRIBUTES | SHGFI_SHELLICONSIZE` lookup** — a generic 32 px type icon. Still
   better than the placeholder, and the *only* stage ever run for a path that does not exist (stages 1–2
   need the item to actually exist).

A missing path never runs stages 1–2 and is never cached (a path that starts missing and later appears must
not keep serving a stale fallback forever — see `IconProvider.GetIcon`'s own remarks on the `default`
timestamp trap this closes). Whichever stage succeeds for an existing path is cached under `(path, snapped
bucket)`; the uniform gray-square placeholder (`IconFactory.CreateBitmap()`) is strictly the last resort,
reached only when even stage 3 fails.

### `IconPixelSize` buckets, snap-up, and the cache key

`IconPixelSize.Snap` rounds a requested pixel size **up** to the next of a fixed set of buckets —
`16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 144, 192, 256` — clamped to `[Min=16, Max=256]`. Two reasons:
(1) the icon cache is keyed by `(path, size)`, and a panel's `IconSize` slider is continuous, so without
bucketing every slider tick would be a new cache key and a new shell round-trip; (2) every bucket is a size
icon authors actually ship (the 100/125/150/200% multiples of the classic 16/32/48/96/128 sizes), so the
shell usually hands back a hand-drawn frame instead of one it has to resample itself. Snapping up (never
down) means WPF only ever scales the result slightly **down** (`BitmapScalingMode.HighQuality`), never up —
the defect this ADR fixes was a 32 px bitmap stretched to 48+ px; the fix must not reintroduce the same
class of blur one step later. `IconPixelSize.FromDip(sizeDip, dpiScale)` converts a panel's DIP `IconSize`
(or the fixed list-mode size) at its DPI scale into a bucket; a non-finite/non-positive input degrades to
1.0 scale / the 48 px default rather than throwing, so a corrupt persisted `IconSize` still gets an icon.

### Downscale, premultiplied alpha, and cache memory

`SIIGBF_BIGGERSIZEOK` can hand back a frame **larger** than requested (for most 72–192 px requests, the
256 px jumbo frame — 256 KB of pixels). `IconFactory.CreateBitmap(NativeBitmapPixels, int)` resamples any
oversized frame down to exactly the target bucket and materializes the result into a fresh, self-contained
buffer (`IconFactory.Downscale`) before freezing — neither the oversized source bitmap nor the
`TransformedBitmap` chain is retained. This keeps the provider's existing count-bounded LRU cache (1024
entries, ADR-012) bounded in *bytes*, not just in entry count. Resampling runs in premultiplied
(`Pbgra32`) space (`FormatConvertedBitmap` → `TransformedBitmap`'s WIC scaler → convert back): filtering
straight-alpha data channel-by-channel mixes the (usually black) color of fully transparent pixels into
anti-aliased edges and draws a dark halo around the icon. Since `IShellItemImageFactory` does not document
whether its bitmap is premultiplied, `IconFactory.DetectPixelFormat` infers it from the data itself: any
pixel with a color channel exceeding its own alpha proves straight alpha; an all-zero alpha channel means no
alpha channel at all (shown opaque, not fully transparent).

### Cache key stays `(path, size)`; a missing path is never cached

Unchanged from before this round: the cache key is `(FilePath, snapped Size)`, ordinal-ignore-case on the
path. What changed is what freshness-gates a cache hit for a *file*: only extensions known to carry an icon
embedded in the file itself (`.exe`, `.dll`, `.ico`, `.lnk`, `.cur`, `.url` —
`IconProvider.FreshnessSensitiveExtensions`) pay for a `LastWriteTimeUtc` check at all; everything else
(directories in particular) uses a stable `default` timestamp so a Folder Portal resync (which touches a
directory's own `LastWriteTimeUtc` on every child add/remove) does not defeat the cache in its hottest path.

### UNC and offline/cloud-placeholder items skip stages 1–2

`IconProvider.ShouldSkipItemStages` routes a UNC path (`\\server\share\...`, including the `\\?\UNC\...`/
`\\.\UNC\...` extended forms) or an item whose attributes mark its content remote
(`FILE_ATTRIBUTE_OFFLINE`/`FILE_ATTRIBUTE_RECALL_ON_OPEN`/`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` — an
online-only OneDrive/cloud-files placeholder, an HSM-migrated file) straight to stage 3, skipping the two
stages that would otherwise open the item and could block the UI thread for seconds on an unreachable share
or a content recall. **Known, accepted residual**: only the item's *own* path/attributes are checked — a
local `.lnk` whose *target* is an unreachable share, or a mapped network drive letter (`Z:\...`), still runs
stage 1, and the existence check at the top of `GetIcon` itself still makes one metadata round-trip over a
UNC share. The complete fix is a dedicated background STA fetch thread (see "Known follow-up" below).

### 200 ms debounce of a size-driven bulk refetch

`ChildWindowViewModel`'s icon-size slider (16–96 DIP, step 4) can cross 8+ snapped buckets while being
dragged, and a monitor DPI change (`ChildWindow.OnDpiChanged`) pushes a new `IconRenderScale` to every panel
at once — both used to synchronously re-fetch every tracked item's icon once per crossed bucket/panel.
`BaseViewModel.RequestedIconPixelSize` itself still updates **synchronously** on every call (a new item
added mid-drag must use the panel's current size), but the actual re-fetch of every already-tracked item —
the expensive part — is collapsed through `IconResizeDebounceWindow = TimeSpan.FromMilliseconds(200)` into
a single call after that much silence, via the same lazily-resolved, degrade-to-immediate-when-unavailable
`IDebounceTimer` pattern `ApplicationStateService` already uses.

### DPI source: `PanelWindowCoordinates.GetWindowScale`

The pixel size requested for a panel's icons is derived from `PanelWindowCoordinates.GetWindowScale(window,
desktopScale.Current)` — the same per-window DPI-scale source the workspace-grid/PerMonitorV2 coordinate
pipeline (ADR-028/ADR-037) already uses, read on `Initialize`/`OnSourceInitialized`/`OnDpiChanged`. Icon
sizing does not introduce a second, independent notion of "the panel's DPI".

## Consequences

Positive:
- Shortcuts and custom-icon folders now show their real icon instead of a generic type icon.
- Icons render at the panel's actual size instead of being upscaled from a fixed 32 px source.
- The LRU cache (ADR-012) stays bounded in bytes, not just entry count, despite `SIIGBF_BIGGERSIZEOK`
  routinely handing back oversized frames.
- A missing path still gets a real, type-specific icon wherever the attribute-only stage can produce one
  (unchanged from the pre-1.0 behavior) — the uniform placeholder is strictly the last resort.

Negative / known follow-up:
- ~~**All icon extraction is still synchronous on the UI thread.**~~ **Closed by ADR-046 (2026-09-30):**
  every `GetIcon`/`GetTypeIcon` call now runs on a dedicated background STA worker queue (two lanes,
  Local/Remote, with a circuit breaker on the remote root) — a generic placeholder, then a type icon, then
  the real icon is swapped in via `FileItem.UpdateIcon` as results arrive. This paragraph is left in place,
  struck through, as the historical record of the gap ADR-046 closes; see ADR-046 for the design.
- Every fallback-stage failure is logged (`IconHandleFailed`, `ShellItemImageFailed`,
  `SystemIconIndexFailed`, `ImageListIconFailed`, `IconBitmapReadFailed`, `IconBitmapDestroyFailed`,
  `IconRetrievalFailed` with a closed-set `stage` field, `IconFreshnessCheckFailed` —
  `docs/Refactoring/Testing/diagnostics-log-events.md`) with only an extension and an error type/HRESULT,
  never a path — consistent with the existing `icon` category's privacy invariant (ADR-027 Amendment).

## Manual verify (Win10/Win11)

A `.lnk` to an application shows the target's real icon (not the generic shortcut icon); a folder with a
custom `desktop.ini` icon shows that icon; dragging the icon-size slider across several buckets does not
visibly stutter; a monitor DPI change re-fetches icons at the new size without a visible flash-to-placeholder
for items whose icon was already cached at the old size; a shortcut to a currently-unreachable network share
does not visibly stall the panel; a OneDrive online-only file's icon does not force a download.

## Related

- ADR-012 (Performance and Resilience Hardening) — the count-bounded LRU icon cache and the `Freeze()`
  invariant for every `ImageSource` leaving Services this ADR builds on.
- ADR-028/ADR-037 (Workspace Grid / PerMonitorV2) — `PanelWindowCoordinates.GetWindowScale`, the shared
  per-window DPI-scale source this ADR reuses rather than duplicating.
- `docs/Refactoring/Testing/diagnostics-log-events.md` — the `icon` category event list, extended by this
  round.
- [ADR-046](ADR-046-background-icon-loading.md) — closes the "known follow-up" above: moves every `GetIcon`
  call off the UI thread onto a background STA worker queue, without changing this ADR's fallback chain,
  cache key, or UNC/offline skip.
