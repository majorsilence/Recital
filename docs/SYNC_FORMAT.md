# Sync folder format

**Format version:** 1 (draft: written since Phase 4, to a local folder only, so it can still change
before Phase 5 syncs it)

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

## Files

JSON is UTF-8, camelCase, indented. Readers ignore unknown properties and skip files they cannot parse.
HLC timestamps are strings `<16-digit wall ms>-<4-digit counter>-<deviceId>`, which sort like the clocks.
Ids are lowercase hex (book ids are SHA-256, device ids random 128-bit).

`format.json`: `{ "formatVersion": 1 }`

`devices/<deviceId>.json`:
```json
{ "formatVersion": 1, "deviceId": "c9b6...", "name": "laptop", "platform": "linux", "lastSeen": "2026-10-14T12:00:00+00:00" }
```

`meta/<bookId>.json`:
```json
{ "formatVersion": 1, "bookId": "3f2a...", "title": "Pride and Prejudice", "authors": ["Jane Austen"],
  "language": "en-GB", "identifier": "url:https://standardebooks.org/...", "fileSize": 1234567,
  "fileName": "pride.epub", "hasCover": true, "addedAt": "...", "addedByDevice": "c9b6..." }
```

`state/<bookId>/<deviceId>.json`:
```json
{ "formatVersion": 1, "bookId": "3f2a...", "deviceId": "c9b6...",
  "progress": {
    "locator": { "cfi": "epubcfi(/6/14[chapter-5]!/4/2/10/1:0)", "spineIndex": 6, "charOffset": 4210, "percent": 0.071 },
    "hlc": "0001760443200000-0000-c9b6...", "updatedAt": "..." },
  "annotations": [] }
```
`annotations` is carried through unchanged until Phase 6 defines it. Merge: highest `progress.hlc`
wins across all files in the folder, conflicted copies included.

`settings/<deviceId>.json`:
```json
{ "formatVersion": 1, "deviceId": "c9b6...",
  "values": { "theme": { "value": "dark", "hlc": "..." }, "fontSize": { "value": "20", "hlc": "..." } } }
```
Keys: `theme` (light/sepia/dark/black), `fontSize`, `fontFamily` (""/serif/sans-serif/monospace),
`lineHeight` ("" or a factor), `justify` (""/justify/left), `margins` (narrow/normal/wide),
`publisherFonts` (true/false). A device writes only keys it changed. Merge: per key, highest `hlc`.

The device id itself lives outside the sync home, in local app data (`device.json`), so copying a sync
folder never makes two devices share an id.
