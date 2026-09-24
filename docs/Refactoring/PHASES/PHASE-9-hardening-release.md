# Phase 9 — Hardening & Release Readiness

## Работы

1. Full manual matrix Windows 10/11.
2. DPI + multi-monitor matrix.
3. Explorer restart, shell crash/restart, sleep/wake.
4. Virtual desktop switching.
5. Stress: 20 panels / 2000 items.
6. Corrupt/missing state recovery.
7. Upgrade from previous stable build.
8. Packaging/update verification.
9. Log redaction/privacy check.
10. Accessibility/readability pass.

## Release gates

- no P0/P1 open;
- resize hang not reproducible in stress matrix;
- no state-loss defects;
- memory stable after repeated panel create/delete;
- startup regression within agreed budget;
- rollback tested.
