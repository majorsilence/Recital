# Recital

A cross-platform EPUB reader built on [Majorsilence.Forms](https://forms.majorsilence.com). Your library
and reading position sync through your own cloud storage (Google Drive, Dropbox, OneDrive, WebDAV,
iCloud Drive or any synced folder), with no server and no accounts. Text-to-speech aims to sound like a
person reading.

Recital is also a deliberate workout for Majorsilence.Forms: every framework rough edge it hits is
logged in [docs/FRAMEWORK_GAPS.md](docs/FRAMEWORK_GAPS.md).

Status: Phase 1 (skeleton). See [docs/PLAN.md](docs/PLAN.md).

## Build

```bash
dotnet build Majorsilence.Recital.slnx -c Release
dotnet test Majorsilence.Recital.slnx -c Release
dotnet run --project hosts/Majorsilence.Recital.Desktop
```

- **Android:** with the `android` workload installed the head builds automatically; force it with
  `-p:EnableAndroidHead=true`. Without the workload it builds as an empty stub.
- **iOS:** macOS only. `dotnet build hosts/Majorsilence.Recital.iOS -p:EnableIOSHead=true`.
- **Browser:** builds with no workload. Running it needs native Skia linked into the WASM bundle,
  which needs the `wasm-tools` workload: `dotnet publish hosts/Majorsilence.Recital.Browser -c Release`.
- **UI baselines:** one PNG per OS. `RECITAL_UPDATE_BASELINES=1 dotnet test tests/Majorsilence.Recital.UI.Tests`
  rewrites this OS's baselines; CI uploads mismatched renders for the other OSes. Review the PNG
  before committing.

## License

MIT. Third-party licenses are in [THIRD_PARTY.md](THIRD_PARTY.md).
