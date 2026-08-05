# Connect Announce

Simple CounterStrikeSharp plugin that replaces connect/disconnect messages and resolves city/country data with `GeoLite2-City.mmdb`.

## Requirements

- CounterStrikeSharp `1.0.371` or newer (.NET 10)

## Installation

Copy the `ConnectAnnounce` folder from the release zip into your plugins folder, so it ends up as:

```text
csgo/addons/counterstrikesharp/plugins/ConnectAnnounce/ConnectAnnounce.dll
```

Do not upload only `ConnectAnnounce.dll`. `ConnectAnnounce.deps.json` must stay next to the DLL or CounterStrikeSharp cannot resolve the plugin dependencies.

The release zip includes `GeoLite2-City.mmdb`, so it can be installed directly. To update the database later, download it from one of these links and replace the file in the plugin folder:

- https://git.io/GeoLite2-City.mmdb
- https://github.com/P3TERX/GeoLite.mmdb/raw/download/GeoLite2-City.mmdb

```text
csgo/addons/counterstrikesharp/plugins/ConnectAnnounce/GeoLite2-City.mmdb
```

## Files

- `ConnectAnnounce.dll`
- `ConnectAnnounce.deps.json`
- `MaxMind.GeoIP2` dependencies
- `data/cannounce_settings.txt`
- `GeoLite2-City.mmdb`

The options JSON is generated on load at:

```text
csgo/addons/counterstrikesharp/configs/plugins/ConnectAnnounce/ConnectAnnounceConfig.json
```

## Messages

Default messages live in:

```text
ConnectAnnounce/data/cannounce_settings.txt
```

Available placeholders:

- `{PLAYERNAME}`
- `{STEAMID}`
- `{PLAYERCOUNTRY}`
- `{PLAYERCOUNTRYSHORT}`
- `{PLAYERCOUNTRYSHORT3}`
- `{PLAYERCITY}`
- `{PLAYERREGION}`
- `{PLAYERIP}`
- `{PLAYERTYPE}`
- `{DISC_REASON_LABEL}`
- `{DISC_REASON}`

## Configuration

`ConnectAnnounceConfig.json` is generated with these defaults. Once it exists it is never rewritten, so keys added by a later version do not appear automatically — delete the file to regenerate it, or add the key by hand.

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

| Key | Meaning |
| --- | --- |
| `ShowEnhancedConnectMessage` | Announce connects with the `messages` / `messages_admin` templates. |
| `ShowEnhancedDisconnectMessage` | Announce disconnects with those templates. Only for players whose arrival was recorded — see below. |
| `ShowEnhancedToAdmins` | Use the `messages_admin` block at all (see below). |
| `AdminMessageMode` | Whether that block is chosen by who connected or by who is reading. |
| `AdminFlag` | Which flag counts as admin. |
| `ShowStandardConnectMessage` | Keep the game's own connect message as well. |
| `ShowStandardDisconnectMessage` | Keep the game's own disconnect message as well. |
| `GeoLiteDatabasePath` | Path to the mmdb, relative to the plugin folder or absolute. |

### Players on a local network

A private address — `192.168.x`, `10.x`, `172.16–31.x`, loopback, and their IPv6 equivalents — is in no GeoIP database, so those players are announced with generic wording: *connected from a Local Area Network | an IP Address*. If everyone reaching your server over the local network is in fact in one place, you can say where:

```json
{
  "LanCountry": "Russia",
  "LanRegion": "Moscow Oblast",
  "LanCity": "Moscow",
  "LanCountryCode": "RU"
}
```

`{PLAYERCOUNTRYSHORT3}` is derived from `LanCountryCode`, so `"RU"` gives `RU` and `RUS`. A code that is not a recognised two-letter country shows `???` there and is reported in the server log. Each key is optional and keeps its original wording when left empty.

This applies to every player on a private address, not to one person — a private address says nothing about who is behind it.

### Rejected connections

A disconnect is only announced for a player whose arrival the plugin recorded. Clients that are turned away before they finish joining — a reserved-slot plugin rejecting a non-VIP from a full server, a ban or whitelist check — never get recorded, so they no longer produce a stream of "disconnected" lines for players who were never really on the server. Players already connected when the plugin is loaded or reloaded are recorded at that moment, so their departures are still announced.

An invalid color name is reported in the server log. On startup that leaves announcements off until `css_ca_reload` succeeds; on a later reload the plugin keeps running on the settings it already had, so a typo cannot silence the server. Keys the plugin does not recognise are ignored.

### Admin announcements

`ShowEnhancedToAdmins` turns the `messages_admin` block on. `AdminMessageMode` decides what that block is for — the two modes answer different questions:

**`"Subject"` (default) — announce *that* an admin joined.**
When the player who connected or left is an admin, **everyone** on the server sees `messages_admin`; otherwise everyone sees `messages`. Use this to make an admin's arrival stand out to the whole server.

**`"Recipient"` — give admins *more detail*.**
Admins see `messages_admin` and everyone else sees `messages`, for **every** announcement. Use this to show admins extra information — region, city, SteamID — while regular players get a short line.

```json
{ "AdminMessageMode": "Recipient" }
```

Because `{PLAYERTYPE}` always describes the player who connected, not the reader, putting it in both blocks gets you both behaviours at once: everyone still sees that an admin arrived, and admins additionally get the detail.

> **Switching to `"Recipient"`? Rewrite `messages_admin` first.** The shipped default starts with a literal `[ADMIN]` prefix, which only makes sense in `"Subject"` mode. In `"Recipient"` mode that block is what *admins read*, so the prefix would label every arriving player as an admin. Replace it with `{PLAYERTYPE}`, or drop it.

Anything other than `"Recipient"` behaves as `"Subject"`; a value that is set but unrecognised also logs a warning.

Which players count as "admin" is controlled by `AdminFlag` (default `@css/generic`). Set it to whatever flag your admins actually have, e.g. `@css/ban`. Players with `@css/root` always count as admin. This flag also drives the `{PLAYERTYPE}` placeholder.

> Note that `messages_admin` is the only place player IPs or SteamIDs are worth putting in `"Recipient"` mode — in `"Subject"` mode that block is shown to the whole server.

## Colors

The colors above apply to the values the placeholders expand to.

`LocationColor` colors all five location placeholders. To split them up, set any of `CountryColor` (`{PLAYERCOUNTRY}`, `{PLAYERCOUNTRYSHORT}`, `{PLAYERCOUNTRYSHORT3}`), `CityColor` (`{PLAYERCITY}`) or `RegionColor` (`{PLAYERREGION}`). Each one is optional — left empty, it uses `LocationColor`:

```json
{
  "LocationColor": "Green",
  "CityColor": "Grey",
  "RegionColor": "Grey"
}
```

Valid CSSSharp color names: `Default`, `White`, `DarkRed`, `Green`, `LightYellow`, `LightBlue`, `Olive`, `Lime`, `Red`, `LightPurple`, `Purple`, `Grey`, `Yellow`, `Gold`, `Silver`, `Blue`, `DarkBlue`, `BlueGrey`, `Magenta`, `LightRed`, `Orange`.

Any of those names can also be used as an inline tag directly inside a message template to color the surrounding text. The shipped `messages_admin` block uses this for its red `[ADMIN]` prefix:

```text
"playerjoin"    "{Red}[ADMIN] {PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}"
```

## Commands

| Command | Permission | Description |
| --- | --- | --- |
| `css_geolist <target>` | `@css/generic` | Print geographical information about the target(s). |
| `css_ca_reload` | `@css/root` | Reload the settings file, config and GeoIP database. |

`@css/root` satisfies both. A target is a name substring, `#userid`, a SteamID, `@me`, or `@all`.
