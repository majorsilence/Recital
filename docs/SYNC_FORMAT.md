# Sync folder format

**Format version:** 1 (synced since Phase 5). A reader that finds a higher `formatVersion` in
`format.json` stops syncing with that folder rather than write to it.

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
- File names are canonical: `<id>.json` (or `.epub`, `.jpg`) where the id is lowercase hex. Any other
  `.json` name in `state/<bookId>/`, `devices/` or `settings/` is a sync client's conflicted copy
  (`<id> (1).json`, `<id> (Laptop's conflicted copy 2026-10-17).json`): read as an extra merge input,
  never written. Other names in `books/`, `meta/` and `covers/`, and anything else in the folder, are
  ignored. Recital never deletes or renames files in the sync home.
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
Merge: highest `progress.hlc` wins across all files in the folder, conflicted copies included.

`annotations` holds this device's latest version of every annotation it created, changed or deleted:
```json
{ "id": "4f0c...-...", "kind": "highlight", "cfi": "epubcfi(/6/14!/4/2,/1:0,/3:12)", "spineIndex": 6,
  "charOffset": 4210, "percent": 0.071, "text": "Call me Ishmael.", "color": "yellow", "note": "...",
  "createdAt": "...", "updatedAt": "...", "hlc": "0001760443200000-0000-c9b6..." }
```
- `kind`: `highlight` (a range CFI; `color` one of yellow/green/blue/pink/purple; `note` optional) or
  `bookmark` (a point CFI; `text` is the start of the page).
- `spineIndex` and `charOffset` locate it when the CFI no longer resolves; `text` and `percent` are
  for lists and exports.
- A deletion is a tombstone: `{ "id", "kind", "deleted": true, "createdAt", "updatedAt", "hlc" }`, with
  the text and note dropped.
- Merge, across every device's file for the book: union by `id`. If any version is a tombstone, the
  annotation is deleted, whatever the clocks say: deletion is final, so a device that was offline
  with an older copy cannot bring it back. Otherwise the highest `hlc` wins.
- Readers keep properties they do not know when they rewrite an annotation.

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

## How a device syncs

Each device keeps a full local copy of the sync home in its app data (`library/Recital/...`, the same
layout) and the app only ever reads and writes that copy, so it works the same offline. The sync engine
(`Majorsilence.Recital.Core.Sync.SyncEngine`) keeps the two in step:

1. **Outbox.** Every write the app makes to one of its own files or a write-once file is recorded in a
   persistent outbox before it happens.
2. **Push.** Each round, the outbox's files are copied to the sync home: own files overwrite the copy
   there; write-once files are written only if the sync home has none.
3. **Pull.** The sync home is listed (or its change feed read), and every file whose ETag differs from the
   one last copied is copied into the local copy: other devices' files, conflicted copies, and write-once
   files (the sync home's version wins over a local one, so two devices that import the same book keep
   the same `meta/` file). A device's own files are pulled only to restore one its local copy lost.
4. **Merge.** Nothing is merged while copying. The library reads every device's files and merges them by
   HLC as described above, so devices holding the same files show the same state.

Rounds run every 30 s, about 2 s after a local write, and with exponential backoff (2 s doubling to
5 min, with jitter) while the sync home cannot be reached. Without a change feed (a local folder),
change detection is a full listing compared by ETag. Write times are not trusted, because sync clients
keep the original machine's time.

## Local files (never synced)

In the device's app data directory:

- `device.json`: `{ "deviceId": "...", "name": "..." }`, this device's id.
- `sync-home.json`: where this device syncs, and never a secret:
  `{ "formatVersion": 1, "kind": "dropbox", "accountId": "dbid:...", "accountName": "me@example.com" }`.
  The `kind` is `localFolder` (with `folder`), `webDav` (with `url` and `user`), `dropbox`, `oneDrive` or
  `googleDrive` (with `accountId` and `accountName`), or `none`. A file with only `folder` (Phase 5) is a
  local folder. Recital writes `Recital/` inside the folder or account.
- Passwords and refresh tokens: only in the OS secure store, under `webdav:<url>|<user>` and
  `oauth:<service>`.
- `library/Recital/...`: the local copy of the sync home.
- `library/sync/<key>/outbox.json`: `{ "formatVersion": 1, "sequence": 12, "paths": { "<path>": <seq> } }`,
  writes waiting to be pushed. `<key>` is the first 16 hex digits of SHA-256 of the sync home's name
  (`local:<folder>`, `webdav:<url>|<user>`, `dropbox:<account id>`, ...), so choosing another sync home
  starts afresh and re-uploads the local library.
- `library/sync/<key>/index.json`: `{ "formatVersion": 1, "remote": "local:...", "attached": true,
  "cursor": null, "pulled": { "<path>": "<etag>" } }`, the sync home's ETag of each file last copied.
