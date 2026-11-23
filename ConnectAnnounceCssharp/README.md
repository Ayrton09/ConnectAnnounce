# Connect Announce

Plugin source. All user-facing documentation (installation, configuration, placeholders, colors and commands) lives in the [repository README](../README.md), so there is only one copy to keep current.

## Building

```bash
dotnet build -c Release
```

Output lands in `bin/Release/net10.0/`. A release is that folder's contents, minus the `.pdb`, laid out as a single `ConnectAnnounce/` folder with `data/cannounce_settings.txt`, `GeoLite2-City.mmdb` and `THIRD-PARTY-NOTICES.txt` alongside the DLL.

The build expects `GeoLite2-City.mmdb` in the repository root (one level above this folder; it is git-ignored, download it yourself, see the [repository README](../README.md#updating-the-geoip-database)). Without it the build still succeeds but the output has no database, and every player shows as an unknown location until you add one. `THIRD-PARTY-NOTICES.txt` is copied to the output by the project and must stay in the release (the GeoLite attribution and the library licenses).

`CounterStrikeSharp.API` is referenced with `ExcludeAssets="runtime"` because the CounterStrikeSharp runtime already provides it; shipping a second copy risks a load conflict. The MaxMind assemblies are not provided by the runtime and must be shipped.

When zipping a release, make sure the archive stores entry names with forward slashes. Windows PowerShell's `Compress-Archive` writes backslashes, which Linux `unzip` treats as part of the filename instead of as directories.
