# Connect Announce

Simple CounterStrikeSharp plugin that replaces connect/disconnect messages and resolves city/country data with `GeoLite2-City.mmdb`.

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Installation

Copy the complete `ConnectAnnounce` folder to:

```text
csgo/addons/counterstrikesharp/plugins/ConnectAnnounce/
```

The release zip includes `GeoLite2-City.mmdb`, so it can be installed directly. To update the database later, download it from one of these links and replace the file in the plugin folder:

- https://git.io/GeoLite2-City.mmdb
- https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-City.mmdb

## Files

- `ConnectAnnounce.dll`
- `ConnectAnnounce.deps.json`
- `GeoLite2-City.mmdb`
- `MaxMind.GeoIP2` dependencies
- `data/cannounce_settings.txt`

The options JSON is generated on load at:

```text
csgo/addons/counterstrikesharp/configs/plugins/ConnectAnnounce/ConnectAnnounceConfig.json
```

Configurable colors in that JSON:

```json
{
  "PlayerNameColor": "Purple",
  "SteamIdColor": "Default",
  "LocationColor": "Green",
  "PlayerIpColor": "Default",
  "PlayerTypeColor": "Default",
  "DisconnectReasonLabelColor": "Green",
  "DisconnectReasonColor": "Green"
}
```

Valid CSSSharp color names: `Default`, `White`, `DarkRed`, `Green`, `LightYellow`, `LightBlue`, `Olive`, `Lime`, `Red`, `LightPurple`, `Purple`, `Grey`, `Yellow`, `Gold`, `Silver`, `Blue`, `DarkBlue`, `BlueGrey`, `Magenta`, `LightRed`, `Orange`.

## Commands

- `css_geolist <target>`
- `css_ca_reload`
