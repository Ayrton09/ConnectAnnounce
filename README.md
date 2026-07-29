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
  "AdminFlag": "@css/generic",
  "ShowStandardConnectMessage": false,
  "ShowStandardDisconnectMessage": false,
  "GeoLiteDatabasePath": "GeoLite2-City.mmdb",
  "PlayerNameColor": "Purple",
  "SteamIdColor": "Default",
  "LocationColor": "Green",
  "PlayerIpColor": "Default",
  "PlayerTypeColor": "Default",
  "DisconnectReasonLabelColor": "Green",
  "DisconnectReasonColor": "Green"
}
```

| Key | Meaning |
| --- | --- |
| `ShowEnhancedConnectMessage` | Announce connects with the `messages` / `messages_admin` templates. |
| `ShowEnhancedDisconnectMessage` | Announce disconnects with those templates. |
| `ShowEnhancedToAdmins` | Announce admins with the `messages_admin` block (see below). |
| `AdminFlag` | Which flag counts as admin. |
| `ShowStandardConnectMessage` | Keep the game's own connect message as well. |
| `ShowStandardDisconnectMessage` | Keep the game's own disconnect message as well. |
| `GeoLiteDatabasePath` | Path to the mmdb, relative to the plugin folder or absolute. |

An invalid color name is reported in the server log. On startup that leaves announcements off until `css_ca_reload` succeeds; on a later reload the plugin keeps running on the settings it already had, so a typo cannot silence the server. Keys the plugin does not recognise are ignored.

### Admin announcements

`ShowEnhancedToAdmins` controls how admins are announced. When `true` and the connecting/disconnecting player is an admin, **everyone** on the server sees the `messages_admin` block instead of the normal `messages` block, so the whole server knows an admin joined or left. Regular players are always announced with `messages`.

Which players count as "admin" is controlled by `AdminFlag` (default `@css/generic`). Set it to whatever flag your admins actually have, e.g. `@css/ban`. Players with `@css/root` always count as admin. This flag also drives the `{PLAYERTYPE}` placeholder.

## Colors

The colors above apply to the values the placeholders expand to. Valid CSSSharp color names: `Default`, `White`, `DarkRed`, `Green`, `LightYellow`, `LightBlue`, `Olive`, `Lime`, `Red`, `LightPurple`, `Purple`, `Grey`, `Yellow`, `Gold`, `Silver`, `Blue`, `DarkBlue`, `BlueGrey`, `Magenta`, `LightRed`, `Orange`.

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
