# Sync folder format

**Format version:** 1 (draft; not written by any build yet, so it can still change. Implementation
lands in Phase 5)

All synced state lives under one folder in the user's chosen sync home (plan section 2). Recital never
writes anywhere else in the user's storage.

```
<SyncRoot>/Recital/
  format.json                         { "formatVersion": 1 }
  books/<bookId>.epub                 bookId = lowercase hex SHA-256 of the file bytes. Write-once.
  meta/<bookId>.json                  title, authors, language, dc:identifier, addedAt, addedByDevice. Write-once.
  covers/<bookId>.jpg                 small thumbnail, generated on import. Write-once.
  state/<bookId>/<deviceId>.json      this device's progress + annotations for this book
  devices/<deviceId>.json             device name, platform, lastSeen
  settings/<deviceId>.json            this device's synced preferences
```

## Rules

- A device writes only its own `state/*/<deviceId>.json`, `devices/<deviceId>.json` and
  `settings/<deviceId>.json`. `books/`, `meta/` and `covers/` are content-addressed and write-once.
- Readers merge all device files: progress by highest HLC, annotations by GUID union (highest HLC
  per GUID, tombstones kept), settings per key by highest HLC.
- `*conflicted copy*` / `(1)` duplicates are read as extra merge inputs and are otherwise ignored.
  Recital never deletes user files.
- No secrets (OAuth tokens, API keys) are ever written here.

The JSON schemas for each file are added in Phase 5.
