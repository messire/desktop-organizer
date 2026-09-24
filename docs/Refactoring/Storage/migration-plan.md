# Storage Migration Plan

1. Add new store side-by-side with legacy `ApplicationSettingsBase`.
2. On startup:
   - if new store exists, load it;
   - otherwise load legacy values and map to schema v1 JSON;
   - write JSON atomically;
   - mark migration completed only after read-back validation.
3. Keep legacy settings untouched for at least one release.
4. All later upgrades use explicit `ISettingsMigration` steps.
5. Import/export DTO is separate from internal persistence envelope.
6. Add command-line recovery switch if needed: `--restore-state-backup`.
