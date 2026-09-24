# Phase 8 — Native Modules (C/C++)

## Модули, где native оправдан

### Desktop Host

- WorkerW/Progman discovery;
- attach/reattach;
- Z-order healing;
- display topology events;
- DPI messages.

### Panel Sizing

- hit testing;
- sizing loop;
- DPI-aware RECT conversion;
- optional subclassing.

### Shell Native

- robust `.lnk` resolution;
- shell item/PIDL operations;
- icon/thumbnail extraction;
- known folders.

### Optional Thumbnail/Icon Cache

Только после profiling.

## ABI guidance

Если C++ DLL:

- small C ABI;
- opaque handles;
- UTF-16 explicit;
- HRESULT/Win32 error surfaced;
- no C++ exceptions across ABI;
- allocation ownership documented;
- x64 first.

## DoD

- native module имеет isolated integration tests;
- crash boundary не валит state silently;
- managed fallback существует для non-critical enhancements;
- perf improvement подтверждён benchmark, если native введён ради performance.
