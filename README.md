# Connect Announce

[![Release](https://img.shields.io/github/v/release/Ayrton09/ConnectAnnounce?style=flat-square&color=success)](https://github.com/Ayrton09/ConnectAnnounce/releases)
[![Downloads](https://img.shields.io/github/downloads/Ayrton09/ConnectAnnounce/total?style=flat-square&label=downloads&color=brightgreen)](https://github.com/Ayrton09/ConnectAnnounce/releases)
[![License](https://img.shields.io/github/license/Ayrton09/ConnectAnnounce?style=flat-square)](LICENSE)
[![CS2](https://img.shields.io/badge/game-Counter--Strike%202-orange?style=flat-square)](https://www.counter-strike.net/)
[![CounterStrikeSharp](https://img.shields.io/badge/CounterStrikeSharp-%E2%89%A5%201.0.375-F5834A?style=flat-square)](https://github.com/roflmuffin/CounterStrikeSharp)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/)

A CounterStrikeSharp plugin for CS2 that replaces the game's connect and disconnect messages with your own templates, including where each player connects from (country, region and city via MaxMind GeoLite2).

```text
Ayrton09 connected from Argentina | Buenos Aires
Ayrton09 (STEAM_1:1:37229756) disconnected! reason: Disconnected
[ADMIN] SomeAdmin connected from the United States | Chicago
```

## Features

- Connect and disconnect messages built from templates with [placeholders](#placeholders): name, SteamID (both formats), country, region, city, IP, admin or player, disconnect reason.
- GeoIP lookup with the bundled `GeoLite2-City.mmdb`, which can be updated and reloaded without a restart.
- A separate template for admins, used either to announce that an admin joined or to show admins extra detail that regular players do not see.
- Colors per placeholder plus inline color tags inside templates.
- Configurable location for players on a local network.
- No spam from addon-download reconnects or from clients rejected before they finish joining.

## Requirements

- CounterStrikeSharp `1.0.375` or newer (.NET 10)

## Installation

1. Download `ConnectAnnounce-vX.Y.Z.zip` from the [latest release](https://github.com/Ayrton09/ConnectAnnounce/releases/latest).
2. Copy the `ConnectAnnounce` folder from the zip into your plugins folder, so it ends up as:

   ```text
   csgo/addons/counterstrikesharp/plugins/ConnectAnnounce/ConnectAnnounce.dll
   ```

3. Start or restart the server. The config file is generated on first load.

Copy the whole folder, not only `ConnectAnnounce.dll`: `ConnectAnnounce.deps.json` and the bundled MaxMind libraries must stay next to the DLL or CounterStrikeSharp cannot resolve the plugin's dependencies.

The folder contains:

| File | Purpose |
| --- | --- |
| `ConnectAnnounce.dll`, `ConnectAnnounce.deps.json` | The plugin |
| `MaxMind.*.dll`, `Microsoft.Extensions.*.dll` | GeoIP dependencies |
| `data/cannounce_settings.txt` | Message templates |
| `GeoLite2-City.mmdb` | GeoIP database |

### Updating

Stop or restart the server instead of copying the files over a running one. The bundled MaxMind libraries can change between versions, and the old ones stay loaded in memory until the process restarts.

Keep your own `data/cannounce_settings.txt` if you have customized it. `ConnectAnnounceConfig.json` is never overwritten by an update (see [Configuration](#configuration)).

### Updating the GeoIP database

The bundled database is current as of each release. To refresh it later, download a new `GeoLite2-City.mmdb`. The official source is MaxMind, which needs a free account: https://dev.maxmind.com/geoip/geolite2-free-geolocation-data. A community mirror that republishes it without an account is https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-City.mmdb. The data is covered by the [GeoLite EULA](https://www.maxmind.com/en/geolite2/eula) either way (see [Third-party notices](#third-party-notices)).

Replace the file in the plugin folder and run `css_ca_reload`:

```text
csgo/addons/counterstrikesharp/plugins/ConnectAnnounce/GeoLite2-City.mmdb
```

If the new file is missing, is not a City database or cannot be opened, the previous database stays loaded and the reason is logged. Reloading an unchanged file does not read it again.

## Messages

Templates live in `ConnectAnnounce/data/cannounce_settings.txt`:

```text
"CountryShow"
{
	"messages"
	{
		"playerjoin"		"{PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}"
		"playerdisc"		"{PLAYERNAME} ({STEAMID}) disconnected! {DISC_REASON_LABEL}{DISC_REASON}"
	}
	"messages_admin"
	{
		"playerjoin"		"{Red}[ADMIN] {PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}"
		"playerdisc"		"{Red}[ADMIN] {PLAYERNAME} disconnected! {DISC_REASON_LABEL}{DISC_REASON}"
	}
}
```

`messages` is the regular block. `messages_admin` is used as described in [Admin announcements](#admin-announcements). Edit the file and run `css_ca_reload` to apply changes.

The file is checked strictly, because its text goes to every player's chat. A missing closing quote, a missing or duplicated root section, unbalanced braces, or a key with no value is rejected with the line number in the server log. A value cannot contain a line break. Inside quotes only `\"` and `\\` are escapes; any other backslash is kept as typed (`\n` is the two characters `\n`, not a new line). A rejected file is handled like an invalid config: on startup there are no announcements until `css_ca_reload` succeeds, and on a reload the previous templates stay active and `css_ca_reload` reports that it failed.

### Placeholders

| Placeholder | Example | Color key |
| --- | --- | --- |
| `{PLAYERNAME}` | `Ayrton09` | `PlayerNameColor` |
| `{STEAMID}` | `STEAM_1:1:37229756` | `SteamIdColor` |
| `{STEAMID64}` | `76561198034725241` | `SteamIdColor` |
| `{PLAYERCOUNTRY}` | `Argentina`, `the United States` | `CountryColor` |
| `{PLAYERCOUNTRYSHORT}` | `AR` | `CountryColor` |
| `{PLAYERCOUNTRYSHORT3}` | `ARG` | `CountryColor` |
| `{PLAYERREGION}` | `Buenos Aires` | `RegionColor` |
| `{PLAYERCITY}` | `La Plata` | `CityColor` |
| `{PLAYERIP}` | `203.0.113.9` | `PlayerIpColor` |
| `{PLAYERTYPE}` | `Admin` or `Player` | `PlayerTypeColor` |
| `{DISC_REASON_LABEL}` | `reason: ` | `DisconnectReasonLabelColor` |
| `{DISC_REASON}` | `Disconnected`, `Timed out`, `Kicked by server` | `DisconnectReasonColor` |

`{PLAYERCOUNTRY}` adds "the" where English needs it (`the United States`, `the Netherlands`), always in lowercase so it reads right mid-sentence. The disconnect placeholders are empty in join messages.

When an address cannot be located, the location placeholders show `an Unknown Country`, `an Unknown Region`, `Somewhere`, `??` and `???`. Players on a local network get their own wording (see [Players on a local network](#players-on-a-local-network)).

A player name is never parsed for placeholders or color tags, so a name like `{PLAYERIP}` or `{Red}` is shown literally. Control characters in a name (below 0x20, and DEL) are removed first, so a name cannot recolor or split the announcement.

## Configuration

The options file is generated on first load at:

```text
csgo/addons/counterstrikesharp/configs/plugins/ConnectAnnounce/ConnectAnnounceConfig.json
```

Once it exists it is never rewritten, so keys added by a later version do not appear in it automatically. Delete the file to regenerate it with every key, or add the new keys by hand. Missing keys use their default; unknown keys are ignored.

Defaults:

```json
{
  "ShowEnhancedConnectMessage": true,
  "ShowEnhancedDisconnectMessage": true,
  "ShowEnhancedToAdmins": true,
  "AdminMessageMode": "Subject",
  "AdminFlag": "@css/generic",
  "ShowStandardConnectMessage": false,
  "ShowStandardDisconnectMessage": false,
  "GeoLiteDatabasePath": "GeoLite2-City.mmdb",
  "PlayerNameColor": "Purple",
  "SteamIdColor": "Default",
  "LocationColor": "Green",
  "CountryColor": "",
  "CityColor": "",
  "RegionColor": "",
  "LanCountry": "",
  "LanRegion": "",
  "LanCity": "",
  "LanCountryCode": "",
  "PlayerIpColor": "Default",
  "PlayerTypeColor": "Default",
  "DisconnectReasonLabelColor": "Green",
  "DisconnectReasonColor": "Green"
}
```

### General

| Key | Meaning |
| --- | --- |
| `ShowEnhancedConnectMessage` | Announce connects with the templates. |
| `ShowEnhancedDisconnectMessage` | Announce disconnects with the templates. Only for players whose arrival was recorded (see [Rejected connections](#rejected-connections)). |
| `ShowStandardConnectMessage` | Also keep the game's own connect message. |
| `ShowStandardDisconnectMessage` | Also keep the game's own disconnect message. |
| `GeoLiteDatabasePath` | Path to the mmdb, relative to the plugin folder or absolute. |

### Admin announcements

| Key | Meaning |
| --- | --- |
| `ShowEnhancedToAdmins` | Use the `messages_admin` block at all. |
| `AdminMessageMode` | `"Subject"` or `"Recipient"`, see below. |
| `AdminFlag` | Which flag counts as admin. Default `@css/generic`. |

The two modes answer different questions:

**`"Subject"` (default): announce *that* an admin joined.**
When the player who connected or left is an admin, **everyone** on the server sees `messages_admin`; otherwise everyone sees `messages`. Use this to make an admin's arrival stand out.

**`"Recipient"`: give admins *more detail*.**
Admins see `messages_admin` and everyone else sees `messages`, for **every** announcement. Use this to show admins region, city, SteamID or IP while regular players get a short line.

```json
{ "AdminMessageMode": "Recipient" }
```

`{PLAYERTYPE}` always describes the player who connected, not the reader, so putting it in both blocks gives you both at once: everyone still sees that an admin arrived, and admins additionally get the detail.

> **Switching to `"Recipient"`? Rewrite `messages_admin` first.** The shipped default starts with a literal `[ADMIN]` prefix, which only makes sense in `"Subject"` mode. In `"Recipient"` mode that block is what *admins read*, so the prefix would label every arriving player as an admin. Replace it with `{PLAYERTYPE}`, or drop it.

> Only put IPs or SteamIDs in `messages_admin` when using `"Recipient"`. In `"Subject"` mode that block is shown to the whole server.

Anything other than `"Recipient"` behaves as `"Subject"`; a value that is set but unrecognised also logs a warning.

Set `AdminFlag` to whatever flag your admins actually have, e.g. `@css/ban`. Players with `@css/root` always count as admin. The flag also drives `{PLAYERTYPE}`.

`AdminFlag` must start with `@` (`@css/ban`, not `css/ban`). A value without it is logged as a warning and the default `@css/generic` is used instead, because CounterStrikeSharp answers "yes" to a flag in that shape for every player listed in `admins.json`, which would make all of them count as admin.

### Players on a local network

Private and local addresses are in no GeoIP database. That covers `192.168.x`, `10.x`, `172.16-31.x`, loopback, CGNAT and Tailscale (`100.64.0.0/10`), IPv4 link-local (`169.254.x`) and their IPv6 equivalents, including link-local `fe80::/10`. Players on those addresses are announced as *connected from a Local Area Network | an IP Address*.

If everyone reaching your server over the local network is in fact in one place, you can say where:

```json
{
  "LanCountry": "Russia",
  "LanRegion": "Moscow Oblast",
  "LanCity": "Moscow",
  "LanCountryCode": "RU"
}
```

| Key | Replaces | Default wording |
| --- | --- | --- |
| `LanCountry` | `{PLAYERCOUNTRY}` | `a Local Area Network` |
| `LanRegion` | `{PLAYERREGION}` | `a Local Subnet` |
| `LanCity` | `{PLAYERCITY}` | `an IP Address` |
| `LanCountryCode` | `{PLAYERCOUNTRYSHORT}`, `{PLAYERCOUNTRYSHORT3}` | `LN`, `LAN` |

Each key is optional and keeps its default wording when left empty. `{PLAYERCOUNTRYSHORT3}` is derived from `LanCountryCode`, so `"RU"` gives `RU` and `RUS`. A code that is not a recognised two-letter country shows `???` there and is reported in the server log.

This applies to every player on a local address, not to one person: a private address says nothing about who is behind it.

### Colors

| Key | Colors |
| --- | --- |
| `PlayerNameColor` | `{PLAYERNAME}` |
| `SteamIdColor` | `{STEAMID}`, `{STEAMID64}` |
| `LocationColor` | All location placeholders, unless one of the three keys below is set |
| `CountryColor` | `{PLAYERCOUNTRY}`, `{PLAYERCOUNTRYSHORT}`, `{PLAYERCOUNTRYSHORT3}` |
| `RegionColor` | `{PLAYERREGION}` |
| `CityColor` | `{PLAYERCITY}` |
| `PlayerIpColor` | `{PLAYERIP}` |
| `PlayerTypeColor` | `{PLAYERTYPE}` |
| `DisconnectReasonLabelColor` | `{DISC_REASON_LABEL}` |
| `DisconnectReasonColor` | `{DISC_REASON}` |

`CountryColor`, `RegionColor` and `CityColor` are optional; left empty, they use `LocationColor`:

```json
{
  "LocationColor": "Green",
  "CityColor": "Grey",
  "RegionColor": "Grey"
}
```

Valid color names: `Default`, `White`, `DarkRed`, `Green`, `LightYellow`, `LightBlue`, `Olive`, `Lime`, `Red`, `LightPurple`, `Purple`, `Grey`, `Yellow`, `Gold`, `Silver`, `Blue`, `DarkBlue`, `BlueGrey`, `Magenta`, `LightRed`, `Orange`.

Any of those names also works as an inline tag inside a template, coloring the text that follows it. The shipped `messages_admin` block uses this for its red `[ADMIN]` prefix:

```text
"playerjoin"    "{Red}[ADMIN] {PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}"
```

An invalid color name is reported in the server log. On startup that leaves announcements off until `css_ca_reload` succeeds; on a later reload the plugin keeps running on the settings it already had, so a typo cannot silence the server.

## Rejected connections

A disconnect is only announced for a player whose arrival the plugin recorded. Clients turned away before Steam authorizes them (a reserved-slot plugin rejecting a non-VIP from a full server, a ban or whitelist check that runs that early) never get recorded, so they do not produce a stream of "disconnected" lines for players who were never really on the server.

Players already connected when the plugin is loaded or reloaded are recorded at that moment, so their departures are still announced. That includes players who are authorized but still loading the map. Players who are not authorized yet are recorded when Steam authorizes them.

Reconnects caused by addon downloads (MultiAddonManager or forced workshop downloads) are recognised and announced only once.

## Commands

| Command | Permission | Description |
| --- | --- | --- |
| `css_geolist <target>` | `@css/generic` | Print the location of the target(s). |
| `css_ca_reload` | `@css/root` | Reload the templates, the config and the GeoIP database. |

`@css/root` satisfies both. Both commands also work from the server console.

A `css_geolist` target is one of:

| Target | Example |
| --- | --- |
| Name substring (the whole text after the command, so spaces work) | `css_geolist John Doe` |
| `#userid` | `css_geolist #3` |
| SteamID64 | `css_geolist 76561198034725241` |
| SteamID2, `STEAM_0` or `STEAM_1` | `css_geolist STEAM_1:1:37229756` |
| SteamID3 | `css_geolist [U:1:74459513]` |
| `@me` or `@all` | `css_geolist @all` |

A purely numeric target that matches no SteamID is then tried as part of a name.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Every player shows `an Unknown Country` | The database did not load. Look for `GeoLite2` in the CounterStrikeSharp log, check `GeoLiteDatabasePath` and that the file is a City database. |
| No announcements at all after the server starts | The config JSON or `cannounce_settings.txt` is invalid (an unknown color name, malformed JSON, an unclosed quote or brace). The log names the problem and the line; fix it and run `css_ca_reload`. On a later `css_ca_reload` an invalid file does not silence anything: the previous settings stay active and the command reports that the reload failed. |
| A new config key has no effect | The config is never rewritten. Add the key by hand or delete the file to regenerate it. |
| The plugin does not load | Only `ConnectAnnounce.dll` was copied, or CounterStrikeSharp is older than 1.0.375. Copy the whole folder. |
| `{PLAYERCOUNTRYSHORT3}` shows `???` for LAN players | `LanCountryCode` is not a valid two-letter country code. |
| You do not see your own join message | Expected: it is sent while your game is still loading. Other players see it. |

## Building

See [ConnectAnnounceCssharp/README.md](ConnectAnnounceCssharp/README.md).

## License

The plugin code is [MIT](LICENSE). The GeoLite2 database and the libraries shipped with the plugin are not covered by that license; see below.

## Third-party notices

This product includes GeoLite Data created by MaxMind, available from https://www.maxmind.com

- The GeoLite2 data is distributed under the [GeoLite EULA](https://www.maxmind.com/en/geolite2/eula), and its copyrightable elements are licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).
- `MaxMind.GeoIP2` and `MaxMind.Db` are Copyright MaxMind, Inc. and licensed under the Apache License 2.0.
- `Microsoft.Extensions.*` are Copyright Microsoft Corporation and licensed under the MIT license.

The full notices, with the license texts, are in [`ConnectAnnounceCssharp/THIRD-PARTY-NOTICES.txt`](ConnectAnnounceCssharp/THIRD-PARTY-NOTICES.txt), which is also shipped in the plugin folder of every release.
