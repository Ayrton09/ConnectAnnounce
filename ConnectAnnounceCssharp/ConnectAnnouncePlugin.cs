using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Logging;

namespace ConnectAnnounceCssharp;

// Matches the requirement stated in the README. CounterStrikeSharp compares this against
// its own build number, so an older server refuses the plugin with a clear message instead
// of loading it and failing later on an API that does not exist there.
[MinimumApiVersion(371)]
public sealed class ConnectAnnouncePlugin : BasePlugin
{
    private const string Version = "1.0.5";
    private const ulong SteamId64Base = 76561197960265728UL;
    private static readonly Regex PlaceholderPattern = new(@"\{[A-Za-z0-9_]+\}", RegexOptions.Compiled);

    private readonly object _fileLock = new();
    private readonly object _geoLock = new();
    // When each player was last seen arriving or leaving transiently. A reconnect inside
    // this window is treated as the same arrival (map change, or a forced addon-download
    // reconnect loop) and is not announced again; a player returning after it is a genuine
    // new arrival. Because the entry ages out on its own, a player who leaves for good can
    // never get stuck permanently un-announceable, which is what keying on "is this player
    // still connected" used to cause.
    // The window covers a single reconnect hop rather than a whole download chain: every
    // hop stamps the entry again, so five minutes is generous.
    private static readonly TimeSpan AnnounceWindow = TimeSpan.FromMinutes(5);
    private readonly Dictionary<ulong, DateTime> _lastSeenAt = [];

    // Last SteamID seen on each slot. AuthorizedSteamID stops resolving on a controller
    // that is being torn down, which is exactly when the disconnect path needs it, so keep
    // our own copy from authorization time. Paired with the UserId of the connection it was
    // recorded for, because slots are reused: without that check a client that disconnects
    // before authorizing would resolve to whoever held the slot last. Bounded by slot count.
    private readonly Dictionary<int, (int UserId, ulong Steam64)> _slotSteamIds = [];
    private ConnectAnnounceConfig _config = new();
    private KeyValuesNode _countryShow = new("CountryShow");
    private DatabaseReader? _geoReader;
    private bool _geoLoadAttempted;
    private bool _filesLoaded;
    private bool _mapChanging;

    // Clients that fail to load the new map drop shortly after it starts, with ordinary
    // reasons like "Timed out" that are indistinguishable from a real departure. Treat
    // the first moments of a map as still transitioning so those are not announced as
    // arrivals and departures. Replaces a timer that used to clear _mapChanging late.
    private static readonly TimeSpan MapSettleWindow = TimeSpan.FromSeconds(15);
    private DateTime _mapStartedAt = DateTime.MinValue;
    private string _dataDirectory = "";
    private string _settingsPath = "";
    private string _configPath = "";

    public override string ModuleName => "Connect Announce";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "Ayrton09";
    public override string ModuleDescription => "Custom connect and disconnect announcements with GeoIP location.";

    public override void Load(bool hotReload)
    {
        // Everything that can throw stays inside the try, including resolving the config
        // path. If it escaped, the handlers and css_ca_reload below would never register
        // and there would be no way to recover without restarting the server.
        try
        {
            _dataDirectory = Path.Combine(ModuleDirectory, "data");
            _settingsPath = Path.Combine(_dataDirectory, "cannounce_settings.txt");
            _configPath = Path.Combine(GetCounterStrikeSharpConfigsPath(), "plugins", "ConnectAnnounce", "ConnectAnnounceConfig.json");

            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            LoadAllFiles();
        }
        catch (Exception ex)
        {
            _filesLoaded = false;
            Logger.LogError(ex, "Connect Announce failed during startup. The plugin will stay loaded but announcements are disabled until css_ca_reload succeeds.");
        }

        RegisterEventHandler<EventPlayerConnect>(OnPlayerConnectPre, HookMode.Pre);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectPre, HookMode.Pre);
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        AddAdminOnlyCommand("css_geolist", "prints geographical information about target(s)", OnGeoListCommand, "@css/generic");
        AddCommand("css_ca_reload", "reloads Connect Announce files", OnReloadCommand);

        // Only on a hot reload. On a cold start the server has no players yet, and asking for
        // the player list before the first map is loaded would be the process's first read of
        // Server.MaxPlayers — a value CounterStrikeSharp caches statically and never
        // refreshes, so a pre-map answer would stick for every plugin for the whole session.
        if (hotReload)
        {
            SeedPlayersAlreadyHere();
        }
    }

    // A departure is only announced for a player we know arrived, so anyone already on the
    // server when the plugin loads has to be recorded here or their eventual departure would
    // be silent.
    private void SeedPlayersAlreadyHere()
    {
        List<CCSPlayerController> players;
        try
        {
            players = GetRealPlayers().ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not list the players already on the server.");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var player in players)
        {
            try
            {
                var steam64 = ResolveSteamId(player);
                if (!steam64.HasValue)
                {
                    continue;
                }

                _lastSeenAt[steam64.Value] = now;
                if (player.UserId is { } userId)
                {
                    _slotSteamIds[player.Slot] = (userId, steam64.Value);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not record a player already on the server.");
            }
        }
    }

    public override void Unload(bool hotReload)
    {
        // Same lock as every other write to the reader; reads take a local snapshot.
        lock (_geoLock)
        {
            _geoReader?.Dispose();
            _geoReader = null;
        }
    }

    private void OnMapStart(string mapName)
    {
        _mapChanging = false;
        _mapStartedAt = DateTime.UtcNow;

        // Drop entries that have aged out. Players who stayed are stamped in OnMapEnd and
        // again by their transition disconnect, and re-authorizing on the new map stamps
        // them a third time, so this should only reach players who are gone. If all of those
        // miss someone who is still here, they are announced arriving once more and their
        // eventual departure goes unannounced, because a departure needs a recorded arrival.
        var cutoff = DateTime.UtcNow - AnnounceWindow;
        foreach (var steam64 in _lastSeenAt.Where(entry => entry.Value < cutoff).Select(entry => entry.Key).ToList())
        {
            _lastSeenAt.Remove(steam64);
        }
    }

    private void OnMapEnd()
    {
        _mapChanging = true;

        // Stamp everyone who is still here, while the controllers are still healthy. The
        // map change drops and re-authorizes them, and that re-authorization must land
        // inside the announce window or they get announced again as if they had just
        // arrived. This is the first of two layers; the transient-disconnect refresh is
        // the second, so suppression survives either one missing a player.

        // Resolve the list inside its own guard: the filter reads entity state during
        // enumeration, so a controller already tearing down can throw there rather than
        // in the loop body.
        List<CCSPlayerController> players;
        try
        {
            players = GetRealPlayers().ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not list players before the map change.");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var player in players)
        {
            // One controller that is already tearing down must not abort the loop and
            // leave everyone after it unstamped.
            try
            {
                var steam64 = ResolveSteamId(player);
                if (steam64.HasValue)
                {
                    _lastSeenAt[steam64.Value] = now;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not record a player before the map change.");
            }
        }
    }

    // AuthorizedSteamID can stop resolving on a controller that is going away, so fall
    // back to what we recorded for that slot when the player authorized. The cached entry
    // is only trusted when it belongs to this same connection: slots get reused, and a
    // client that disconnects before authorizing would otherwise be reported under the
    // previous occupant's SteamID.
    private ulong? ResolveSteamId(CCSPlayerController player)
    {
        ulong? steam64;
        try
        {
            steam64 = player.AuthorizedSteamID?.SteamId64;
        }
        catch (Exception)
        {
            // Reading it off a controller that is going away can throw rather than return
            // null. That is exactly the case the cache below exists for, so fall through
            // instead of losing the announcement.
            steam64 = null;
        }

        if (steam64.HasValue)
        {
            return steam64;
        }

        return _slotSteamIds.TryGetValue(player.Slot, out var cached) && cached.UserId == player.UserId
            ? cached.Steam64
            : null;
    }

    private HookResult OnPlayerConnectPre(EventPlayerConnect @event, GameEventInfo info)
    {
        if (!_filesLoaded)
        {
            return HookResult.Continue;
        }

        if (!_config.ShowStandardConnectMessage)
        {
            info.DontBroadcast = true;
        }

        return HookResult.Continue;
    }

    private void OnClientAuthorized(int playerSlot, SteamID steamId)
    {
        // This runs while a client is still joining, reading entity state that can go away
        // underneath it. Nothing here is worth letting an exception escape into the
        // framework's listener dispatch over.
        try
        {
            AnnounceAuthorizedPlayer(playerSlot, steamId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not announce a connecting player.");
        }
    }

    private void AnnounceAuthorizedPlayer(int playerSlot, SteamID steamId)
    {
        if (!_filesLoaded)
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (!IsConnectingPlayer(player))
        {
            return;
        }

        // Record the player FIRST. Their departure is only announced if this entry exists, so
        // nothing that can fail — every controller property is a native read — may run ahead
        // of it and cost them their leaving message.
        // The timestamp is refreshed on every authorization, including suppressed ones, so a
        // long chain of reconnects keeps sliding the window instead of ageing out midway and
        // announcing the same arrival twice.
        var steam64 = steamId.SteamId64;
        var now = DateTime.UtcNow;
        var alreadyAnnounced = _lastSeenAt.TryGetValue(steam64, out var lastAnnounced) &&
                               now - lastAnnounced < AnnounceWindow;
        _lastSeenAt[steam64] = now;

        // Best-effort: only used as a fallback when AuthorizedSteamID stops resolving later.
        try
        {
            if (player!.UserId is { } userId)
            {
                _slotSteamIds[playerSlot] = (userId, steam64);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not cache the SteamID for slot {Slot}.", playerSlot);
        }

        if (alreadyAnnounced)
        {
            return;
        }

        var steamKey = GetSteamKey(steamId);
        if (_config.ShowEnhancedConnectMessage)
        {
            var subjectIsAdmin = IsAdmin(steamId);
            var playerMsg = GetCountryMessage("messages", "playerjoin");
            var adminMsg = GetCountryMessage("messages_admin", "playerjoin");
            PrintEnhancedMessage(player!, subjectIsAdmin, playerMsg, adminMsg, steamKey);
        }
    }

    private HookResult OnPlayerDisconnectPre(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (!_filesLoaded)
        {
            return HookResult.Continue;
        }

        if (!_config.ShowStandardDisconnectMessage)
        {
            info.DontBroadcast = true;
        }

        // Everything past this point reads a controller that is on its way out. Suppressing
        // the stock message above already happened, so a failure here must not change the
        // hook's result.
        try
        {
            AnnounceDisconnectingPlayer(@event);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not announce a disconnecting player.");
        }

        return HookResult.Continue;
    }

    private void AnnounceDisconnectingPlayer(EventPlayerDisconnect @event)
    {
        var player = @event.Userid;
        if (!IsRealPlayer(player))
        {
            return;
        }

        var steam64 = ResolveSteamId(player!);

        // Transient disconnects are not real departures: the player is re-authorized right
        // afterwards (map change, or an addon-download reconnect loop). Slide their entry
        // forward so that re-authorization lands inside the announce window instead of
        // looking like a new arrival. Written unconditionally rather than only refreshing
        // an existing entry, so it still holds if the sweep already removed it.
        var settling = DateTime.UtcNow - _mapStartedAt < MapSettleWindow;
        if (_mapChanging || settling || IsTransientDisconnectReason(@event.Reason))
        {
            if (steam64.HasValue)
            {
                _lastSeenAt[steam64.Value] = DateTime.UtcNow;
            }

            return;
        }

        if (!steam64.HasValue)
        {
            return;
        }

        // Only announce a departure for someone whose arrival we know about. A client that
        // is authorized and then immediately rejected — a reserved-slot plugin turning away
        // a non-VIP from a full server, a ban check, a whitelist — was never announced
        // arriving, and announcing it produces a stream of "disconnected" lines for players
        // who were never really here. An entry exists for anyone announced arriving, present
        // at a map change, or already connected when the plugin loaded, so a genuine
        // departure is never lost.
        var hadArrived = _lastSeenAt.Remove(steam64.Value);
        _slotSteamIds.Remove(player!.Slot);

        if (hadArrived && _config.ShowEnhancedDisconnectMessage)
        {
            // Resolve both from our own copy of the SteamID: reading it off a controller
            // that is going away can come back empty, which would render the message with
            // a blank {STEAMID} and announce an admin as an ordinary player.
            var subjectIsAdmin = IsAdmin(new SteamID(steam64.Value));
            var steamKey = ToSteam2(steam64.Value, universe: 1);
            var reason = GetDisconnectReason(@event.Reason);
            var playerMsg = GetCountryMessage("messages", "playerdisc");
            var adminMsg = GetCountryMessage("messages_admin", "playerdisc");
            PrintEnhancedMessage(player!, subjectIsAdmin, playerMsg, adminMsg, steamKey, reason);
        }
    }

    private void OnGeoListCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!_filesLoaded)
        {
            command.ReplyToCommand("[CA] Connect Announce files are not loaded. Fix the logged startup error and run css_ca_reload.");
            return;
        }

        if (command.ArgCount < 2)
        {
            command.ReplyToCommand("[CA] Usage: css_geolist <name, #userid, steamid, @me or @all>");
            return;
        }

        var pattern = command.ArgByIndex(1);
        var targets = FindTargets(caller, pattern).ToList();
        if (targets.Count == 0)
        {
            command.ReplyToCommand($"[CA] No players matched '{pattern}'.");
            return;
        }

        if (_geoReader == null)
        {
            command.ReplyToCommand("[CA] No GeoLite2 database is loaded, so locations will be unknown.");
        }

        foreach (var target in targets)
        {
            var location = LookupLocation(target);
            command.ReplyToCommand($"{target.PlayerName} from {location.City} in {location.Region}/{location.Country}");
        }
    }

    private void OnReloadCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!HasPermission(caller, "@css/root"))
        {
            command.ReplyToCommand("[CA] You do not have access to this command.");
            return;
        }

        LoadAllFiles();

        if (_filesLoaded)
        {
            // Players who connected while the files were not loaded were never recorded, so
            // pick them up now rather than letting their departure go unannounced.
            SeedPlayersAlreadyHere();
        }

        command.ReplyToCommand(_filesLoaded
            ? "[CA] Reloaded Connect Announce configuration."
            : "[CA] Reload failed. Check the server log for the exact error.");
    }

    private void PrintEnhancedMessage(CCSPlayerController subject, bool subjectIsAdmin, string playerMessage, string adminMessage, string? steamKey = null, string? disconnectReason = null)
    {
        // Two ways to use the admin template, chosen by AdminMessageMode:
        //   Subject   - the whole server sees the admin template when the player who
        //               connected or left is an admin, so everyone knows an admin is on.
        //   Recipient - admins see the admin template and everyone else sees the normal
        //               one, for every announcement, so admins get the extra detail.
        // ShowEnhancedToAdmins is the master switch for both.
        var enhancedForAdmins = _config.ShowEnhancedToAdmins;
        var perRecipient = enhancedForAdmins && UseRecipientAdminMessages;

        // Format up front, while the subject is still readable. Recipient mode needs both
        // variants, but each is still built exactly once here — never once per recipient,
        // which would repeat the GeoIP lookup for every player on the server.
        var publicLine = FormatMessage(
            !perRecipient && enhancedForAdmins && subjectIsAdmin ? adminMessage : playerMessage,
            subject, subjectIsAdmin, steamKey, disconnectReason);
        var adminLine = perRecipient
            ? FormatMessage(adminMessage, subject, subjectIsAdmin, steamKey, disconnectReason)
            : null;

        // Capture the logger and the flag rather than reaching for members from inside the
        // callback, so the closure does not hold on to the plugin instance.
        var logger = Logger;
        var adminFlag = ResolvedAdminFlag;

        // Defer the broadcast so it never runs while a connect or disconnect is still being
        // processed, and resolve the recipients inside the callback so the list reflects who
        // is on the server at send time. NextWorldUpdate rather than NextFrame because
        // NextFrame does not run while the server is hibernating.
        Server.NextWorldUpdate(() =>
        {
            // The recipient limit is the engine's, not the configured slot count.
            const int recipientLimit = 64;

            // Resolve the list inside its own guard. GetRealPlayers is lazy and its filter
            // reads entity state, so a player going away can throw while the list is being
            // enumerated, not only when the message is sent.
            List<CCSPlayerController> recipients;
            try
            {
                recipients = GetRealPlayers().ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not resolve announcement recipients.");
                return;
            }

            // One stale recipient must not swallow the message for everyone after them.
            foreach (var player in recipients)
            {
                try
                {
                    // The engine native behind PrintToChat sets a raw slot in a recipient
                    // bit vector with no bounds check of its own, so validate it here.
                    if (player.Slot < 0 || player.Slot >= recipientLimit)
                    {
                        continue;
                    }

                    // adminLine is only non-null in recipient mode, so subject mode does
                    // not pay for an admin lookup per player.
                    var line = adminLine is not null && IsAdminRecipient(player, adminFlag)
                        ? adminLine
                        : publicLine;

                    player.PrintToChat(" " + line);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not deliver an announcement to a player.");
                }
            }
        });
    }

    private string FormatMessage(string rawMessage, CCSPlayerController? player, bool isAdmin, string? steamKeyOverride = null, string? disconnectReason = null)
    {
        var message = ApplyColorTags(rawMessage);
        if (player == null)
        {
            return message;
        }

        // Read the address once and reuse it: every property read on a controller is a
        // native round-trip, and this runs inside the connect/disconnect path.
        var ip = ExtractIp(player.IpAddress);
        var location = LookupLocation(ip);
        var steamKey = steamKeyOverride ?? GetSteamKey(player) ?? "";
        var playerType = isAdmin ? "Admin" : "Player";

        // Country, city and region can each be given their own colour; whichever are left
        // empty fall back to LocationColor, which is what every existing config uses.
        var countryColor = Or(_config.CountryColor, _config.LocationColor);
        var cityColor = Or(_config.CityColor, _config.LocationColor);
        var regionColor = Or(_config.RegionColor, _config.LocationColor);

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{PLAYERNAME}"] = ChatColorTagExtensions.Colorize(_config.PlayerNameColor, player.PlayerName),
            ["{STEAMID}"] = ChatColorTagExtensions.Colorize(_config.SteamIdColor, steamKey),
            ["{PLAYERCOUNTRY}"] = ChatColorTagExtensions.Colorize(countryColor, AddThePrefix(location.Country, location.CountryCode2)),
            ["{PLAYERCOUNTRYSHORT}"] = ChatColorTagExtensions.Colorize(countryColor, location.CountryCode2),
            ["{PLAYERCOUNTRYSHORT3}"] = ChatColorTagExtensions.Colorize(countryColor, location.CountryCode3),
            ["{PLAYERCITY}"] = ChatColorTagExtensions.Colorize(cityColor, location.City),
            ["{PLAYERREGION}"] = ChatColorTagExtensions.Colorize(regionColor, location.Region),
            ["{PLAYERIP}"] = ChatColorTagExtensions.Colorize(_config.PlayerIpColor, ip),
            ["{PLAYERTYPE}"] = ChatColorTagExtensions.Colorize(_config.PlayerTypeColor, playerType),
            ["{DISC_REASON_LABEL}"] = ChatColorTagExtensions.Colorize(_config.DisconnectReasonLabelColor, "reason: "),
            ["{DISC_REASON}"] = ChatColorTagExtensions.Colorize(_config.DisconnectReasonColor, disconnectReason ?? "")
        };

        // Single pass over the template: substituted values are never re-scanned,
        // so a player whose name contains a token like {PLAYERIP} cannot inject it.
        return PlaceholderPattern.Replace(message, match =>
            replacements.TryGetValue(match.Value, out var value) ? value : match.Value);
    }

    private GeoLocation LookupLocation(CCSPlayerController player)
    {
        return LookupLocation(ExtractIp(player.IpAddress));
    }

    private GeoLocation LookupLocation(string ipText)
    {
        if (!IPAddress.TryParse(ipText, out var address))
        {
            return GeoLocation.Unknown;
        }

        if (IsLanIp(address))
        {
            return GeoLocation.Lan;
        }

        // Read the field once so a reload cannot swap it midway through this method. This
        // does not stop a reload disposing the reader we captured; that surfaces as a
        // caught exception below and a single unknown location, which is acceptable.
        var reader = _geoReader;
        if (reader == null)
        {
            return GeoLocation.Unknown;
        }

        try
        {
            var city = reader.City(address);
            var countryCode = city.Country.IsoCode ?? "";
            return new GeoLocation(
                FirstEnglishName(city.City.Names, "Somewhere"),
                FirstEnglishName(city.MostSpecificSubdivision.Names, "an Unknown Region"),
                city.Country.Name ?? "an Unknown Country",
                string.IsNullOrWhiteSpace(countryCode) ? "??" : countryCode,
                ToIso3(countryCode));
        }
        catch (AddressNotFoundException)
        {
            return GeoLocation.Unknown;
        }
        catch (GeoIP2Exception ex)
        {
            Logger.LogWarning(ex, "GeoIP lookup failed for {Ip}", ipText);
            return GeoLocation.Unknown;
        }
        catch (InvalidDatabaseException ex)
        {
            Logger.LogWarning(ex, "GeoLite2 database is corrupt while looking up {Ip}", ipText);
            return GeoLocation.Unknown;
        }
        catch (Exception ex)
        {
            // Nothing a lookup can throw is worth losing the announcement over, and an
            // exception escaping here would reach the connect/disconnect handler.
            Logger.LogWarning(ex, "Unexpected GeoIP failure for {Ip}", ipText);
            return GeoLocation.Unknown;
        }
    }

    private void LoadAllFiles()
    {
        lock (_fileLock)
        {
            // Each loader validates before it publishes, so a failure never leaves a
            // half-parsed file live. The sequence as a whole is not atomic though: an
            // earlier step may already have applied while a later one failed, so the log
            // says the state is mixed rather than claiming nothing changed. Staying loaded
            // beats going silent — a reload of a bad file should not be worse than not
            // reloading at all.
            var wasLoaded = _filesLoaded;

            try
            {
                LoadConfig();
                LoadCountryMessages();
                ResetGeoDatabase();
                LoadGeoDatabase();
                _filesLoaded = true;
            }
            catch (Exception ex)
            {
                _filesLoaded = wasLoaded;
                Logger.LogError(ex, wasLoaded
                    ? "Connect Announce could not finish reloading. Announcements stay on, but some settings may still be the previous ones. Fix the error above and run css_ca_reload again."
                    : "Connect Announce could not load its files.");
            }
        }
    }

    private void LoadGeoDatabase()
    {
        lock (_geoLock)
        {
            if (_geoReader != null || _geoLoadAttempted)
            {
                return;
            }

            _geoLoadAttempted = true;

            // A missing or blank path is a configuration mistake, not a reason to throw out
            // of here: an exception would escape into LoadAllFiles and abort the rest of the
            // reload, leaving the plugin running with no geo data and no clear explanation.
            var configuredPath = _config.GeoLiteDatabasePath;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                Logger.LogWarning("GeoLiteDatabasePath is empty. GeoIP placeholders will use unknown values.");
                return;
            }

            var mmdb = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(ModuleDirectory, configuredPath);
            if (!File.Exists(mmdb))
            {
                Logger.LogWarning("GeoLite2 database was not found at {Path}. GeoIP placeholders will use unknown values.", mmdb);
                return;
            }

            DatabaseReader? reader = null;
            try
            {
                reader = new DatabaseReader(mmdb, FileAccessMode.Memory);

                // A Country or ASN database opens happily but throws on every City lookup,
                // which would break every announcement while the log still said the
                // database had loaded. Reject it here instead, with the reason. Enterprise
                // databases do support city lookups, so they are accepted too.
                var databaseType = reader.Metadata.DatabaseType;
                if (!databaseType.Contains("City", StringComparison.OrdinalIgnoreCase) &&
                    !databaseType.Contains("Enterprise", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("{Path} is a '{Type}' database, which cannot answer city lookups. A GeoLite2-City database is required; GeoIP placeholders will use unknown values.", mmdb, databaseType);
                    reader.Dispose();
                    return;
                }

                _geoReader = reader;
                Logger.LogInformation("Loaded GeoLite2 database from {Path}", mmdb);
            }
            catch (Exception ex)
            {
                // Dispose the local: if the failure came from inspecting the metadata, the
                // reader was opened but never stored, so _geoReader would not reach it.
                reader?.Dispose();
                _geoReader = null;
                Logger.LogError(ex, "GeoLite2 database could not be loaded from {Path}. GeoIP placeholders will use unknown values.", mmdb);
            }
        }
    }

    private void ResetGeoDatabase()
    {
        lock (_geoLock)
        {
            _geoReader?.Dispose();
            _geoReader = null;
            _geoLoadAttempted = false;
        }
    }

    private void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            var defaults = new ConnectAnnounceConfig();
            ValidateConfig(defaults);
            _config = defaults;

            // Writing the file is a convenience, not a prerequisite. A read-only configs
            // directory should not stop the plugin from running on its defaults.
            try
            {
                WriteConfig();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not write {Path}. Running on default settings.", _configPath);
            }

            return;
        }

        // Validate before publishing: a reload of a bad file must leave the settings the
        // server is currently running on untouched.
        var json = File.ReadAllText(_configPath);
        var candidate = JsonSerializer.Deserialize<ConnectAnnounceConfig>(json, JsonOptions) ?? new ConnectAnnounceConfig();
        ValidateConfig(candidate);
        _config = candidate;

        // The config is never rewritten on load. Missing keys fall back to their
        // defaults in memory, so the plugin works fine without touching the file.
        // To pick up keys added in a newer version, delete the file and let it
        // regenerate, or add the key by hand.
    }

    private void WriteConfig()
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions));
    }

    private static string GetCounterStrikeSharpConfigsPath()
    {
        var apiDirectory = Path.GetDirectoryName(typeof(BasePlugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(apiDirectory))
        {
            throw new InvalidOperationException("CounterStrikeSharp API directory could not be resolved.");
        }

        var rootDirectory = Directory.GetParent(apiDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new InvalidOperationException($"CounterStrikeSharp root directory could not be resolved from '{apiDirectory}'.");
        }

        return Path.Combine(rootDirectory, "configs");
    }

    private void ValidateConfig(ConnectAnnounceConfig config)
    {
        var configuredColors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(config.PlayerNameColor)] = config.PlayerNameColor,
            [nameof(config.SteamIdColor)] = config.SteamIdColor,
            [nameof(config.LocationColor)] = config.LocationColor,
            [nameof(config.PlayerIpColor)] = config.PlayerIpColor,
            [nameof(config.PlayerTypeColor)] = config.PlayerTypeColor,
            [nameof(config.DisconnectReasonLabelColor)] = config.DisconnectReasonLabelColor,
            [nameof(config.DisconnectReasonColor)] = config.DisconnectReasonColor
        };

        // Optional: empty means "inherit LocationColor", so only a non-empty value is checked.
        var optionalColors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(config.CountryColor)] = config.CountryColor,
            [nameof(config.CityColor)] = config.CityColor,
            [nameof(config.RegionColor)] = config.RegionColor
        };

        foreach (var (name, color) in optionalColors)
        {
            if (!string.IsNullOrWhiteSpace(color))
            {
                configuredColors[name] = color.Trim();
            }
        }

        foreach (var (name, color) in configuredColors)
        {
            if (!ChatColorTagExtensions.IsKnownColor(color))
            {
                throw new InvalidOperationException($"{name} has invalid CSSSharp chat color '{color}'. Valid colors: {string.Join(", ", ChatColorTagExtensions.AvailableColorNames)}");
            }
        }

        // A malformed flag is not worth refusing to load over, but silently matching
        // nobody is the exact symptom that is impossible to diagnose from in-game.
        var adminFlag = config.AdminFlag?.Trim();
        if (!string.IsNullOrWhiteSpace(adminFlag) && !adminFlag.StartsWith('@'))
        {
            Logger.LogWarning("AdminFlag '{Flag}' does not start with '@', so it will not match any admin. Did you mean '{Suggestion}'?", adminFlag, "@" + adminFlag.TrimStart('#'));
        }

        // Same reasoning: an unrecognised mode falls back to the long-standing behaviour
        // rather than refusing to load, but it must not do so silently.
        var mode = config.AdminMessageMode?.Trim();
        if (!string.IsNullOrEmpty(mode) &&
            !mode.Equals(SubjectAdminMessageMode, StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals(RecipientAdminMessageMode, StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogWarning("AdminMessageMode '{Mode}' is not recognised. Valid values are '{Subject}' and '{Recipient}'; falling back to '{Fallback}'.", mode, SubjectAdminMessageMode, RecipientAdminMessageMode, SubjectAdminMessageMode);
        }
    }

    private void LoadCountryMessages()
    {
        if (!File.Exists(_settingsPath))
        {
            _countryShow = DefaultCountryShow();

            try
            {
                File.WriteAllText(_settingsPath, KeyValuesSerializer.Serialize(_countryShow));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not write {Path}. Running on default messages.", _settingsPath);
            }

            return;
        }

        _countryShow = KeyValuesParser.Parse(File.ReadAllText(_settingsPath));
        EnsureDefaultCountryMessages();
    }

    private string GetCountryMessage(string sectionName, string key)
    {
        if (_countryShow.Children.TryGetValue(sectionName, out var section) &&
            section.Children.TryGetValue(key, out var valueNode) &&
            !string.IsNullOrEmpty(valueNode.Value))
        {
            return valueNode.Value;
        }

        var defaults = DefaultCountryShow();
        return defaults.Children[sectionName].Children[key].Value ?? "";
    }

    private void EnsureDefaultCountryMessages()
    {
        var defaults = DefaultCountryShow();
        foreach (var (sectionName, section) in defaults.Children)
        {
            var targetSection = _countryShow.GetOrAddChild(sectionName);
            foreach (var (key, value) in section.Children)
            {
                if (!targetSection.Children.ContainsKey(key))
                {
                    targetSection.SetValue(key, value.Value ?? "");
                }
            }
        }
    }

    private static KeyValuesNode DefaultCountryShow()
    {
        var root = new KeyValuesNode("CountryShow");

        var messages = root.GetOrAddChild("messages");
        messages.SetValue("playerjoin", "{PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}");
        messages.SetValue("playerdisc", "{PLAYERNAME} ({STEAMID}) disconnected! {DISC_REASON_LABEL}{DISC_REASON}");

        var messagesAdmin = root.GetOrAddChild("messages_admin");
        messagesAdmin.SetValue("playerjoin", "{Red}[ADMIN] {PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}");
        messagesAdmin.SetValue("playerdisc", "{Red}[ADMIN] {PLAYERNAME} disconnected! {DISC_REASON_LABEL}{DISC_REASON}");

        return root;
    }

    private void AddAdminOnlyCommand(string name, string description, CommandInfo.CommandCallback callback, string permission)
    {
        AddCommand(name, description, (player, command) =>
        {
            if (!HasPermission(player, permission))
            {
                command.ReplyToCommand("[CA] You do not have access to this command.");
                return;
            }

            callback(player, command);
        });
    }

    private bool HasPermission(CCSPlayerController? player, string permission)
    {
        return player == null ||
               AdminManager.PlayerHasPermissions(player, "@css/root") ||
               AdminManager.PlayerHasPermissions(player, permission);
    }

    private const string SubjectAdminMessageMode = "Subject";
    private const string RecipientAdminMessageMode = "Recipient";

    // Only an explicit, recognised "Recipient" switches modes. Anything else — a typo, an
    // empty string, a key missing from an older config — keeps the original behaviour.
    private bool UseRecipientAdminMessages =>
        RecipientAdminMessageMode.Equals(_config.AdminMessageMode?.Trim(), StringComparison.OrdinalIgnoreCase);

    // An optional colour override, falling back when it is unset.
    private static string Or(string? preferred, string fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
    }

    private string ResolvedAdminFlag
    {
        get
        {
            var flag = _config.AdminFlag?.Trim();
            return string.IsNullOrEmpty(flag) ? "@css/generic" : flag;
        }
    }

    private bool IsAdmin(SteamID? steamId)
    {
        return HasAdminFlag(steamId, ResolvedAdminFlag);
    }

    // Resolve admin status from the SteamID, not the controller. The controller overload of
    // PlayerHasPermissions returns false unless the player is already in the Connected
    // state, which is NOT guaranteed at OnClientAuthorized time, so a connecting admin
    // would otherwise be announced as a normal player. The SteamID overload has no such
    // guard and admin data is keyed by SteamID.
    // Static, and takes the flag as an argument, so the deferred broadcast can call it
    // without its closure capturing the plugin instance.
    private static bool HasAdminFlag(SteamID? steamId, string adminFlag)
    {
        if (steamId == null)
        {
            return false;
        }

        return AdminManager.PlayerHasPermissions(steamId, "@css/root") ||
               AdminManager.PlayerHasPermissions(steamId, adminFlag);
    }

    // Admin status of a message RECIPIENT. Recipients come from GetRealPlayers, which only
    // returns fully connected players, so reading their SteamID here is the easy case.
    // Fails closed: if it cannot be determined, treat them as a regular player so the admin
    // variant — which may carry a SteamID or IP — is never shown to someone unverified.
    private static bool IsAdminRecipient(CCSPlayerController player, string adminFlag)
    {
        try
        {
            return HasAdminFlag(player.AuthorizedSteamID, adminFlag);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IEnumerable<CCSPlayerController> FindTargets(CCSPlayerController? caller, string pattern)
    {
        var players = GetRealPlayers().ToList();
        if (pattern.Equals("@all", StringComparison.OrdinalIgnoreCase))
        {
            return players;
        }

        if (pattern.Equals("@me", StringComparison.OrdinalIgnoreCase))
        {
            return caller != null && IsRealPlayer(caller) ? [caller] : [];
        }

        if (pattern.StartsWith('#') && int.TryParse(pattern[1..], out var userId))
        {
            var byUserId = players.FirstOrDefault(player => player.UserId == userId);
            return byUserId != null ? [byUserId] : [];
        }

        if (ulong.TryParse(pattern, out var steam64))
        {
            var bySteam = players.FirstOrDefault(player => player.AuthorizedSteamID?.SteamId64 == steam64);
            return bySteam != null ? [bySteam] : [];
        }

        var matches = players.Where(player =>
                player.PlayerName.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetSteamKey(player), pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches;
    }

    // Static so a deferred callback does not capture the plugin instance and keep an
    // unloaded plugin alive across a hot reload.
    private static IEnumerable<CCSPlayerController> GetRealPlayers()
    {
        return Utilities.GetPlayers().Where(IsRealPlayer)!;
    }

    // HLTV/SourceTV slots are excluded: they are relay clients, not people to announce
    // to, so they should never receive or generate an announcement.
    private static bool IsRealPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsBot: false, IsHLTV: false, UserId: not null };
    }

    private static bool IsConnectingPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsBot: false, IsHLTV: false };
    }

    private static string? GetSteamKey(CCSPlayerController player)
    {
        var steam64 = player.AuthorizedSteamID?.SteamId64;
        return steam64 == null ? null : ToSteam2(steam64.Value, universe: 1);
    }

    private static string GetSteamKey(SteamID steamId)
    {
        return ToSteam2(steamId.SteamId64, universe: 1);
    }

    private static string ToSteam2(ulong steam64, int universe)
    {
        if (steam64 < SteamId64Base)
        {
            return steam64.ToString(CultureInfo.InvariantCulture);
        }

        var accountId = steam64 - SteamId64Base;
        var authServer = accountId % 2;
        var authId = (accountId - authServer) / 2;
        return $"STEAM_{universe}:{authServer}:{authId}";
    }

    private static string ApplyColorTags(string input)
    {
        return input.ReplaceColorTags();
    }

    private static string ExtractIp(string? ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return "";
        }

        var ip = ipAddress.Trim();
        if (ip.StartsWith('['))
        {
            var end = ip.IndexOf(']');
            return end > 0 ? ip[1..end] : ip;
        }

        var lastColon = ip.LastIndexOf(':');
        if (lastColon > -1 && ip.Count(ch => ch == ':') == 1)
        {
            return ip[..lastColon];
        }

        return ip;
    }

    private static bool IsLanIp(IPAddress address)
    {
        // ::ffff:192.168.1.10 is a private IPv4 address wearing an IPv6 shape; without
        // this it would take the IPv6 branch, match none of the local checks, and be
        // treated as a public address.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string FirstEnglishName(IReadOnlyDictionary<string, string> names, string fallback)
    {
        return names.TryGetValue("en", out var english) && !string.IsNullOrWhiteSpace(english)
            ? english
            : names.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? fallback;
    }

    private static string ToIso3(string iso2)
    {
        if (string.IsNullOrWhiteSpace(iso2))
        {
            return "???";
        }

        try
        {
            return new RegionInfo(iso2).ThreeLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return "???";
        }
    }

    // Countries whose English name reads as "the <name>". Keyed on the ISO code because
    // country names change and substring matching got it wrong: "Island" also matched
    // Christmas Island and Norfolk Island, which do not take an article.
    private static readonly HashSet<string> ArticleCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // States and territories
        "AE", "BS", "CD", "CF", "DO", "GB", "GM", "IM", "KM",
        "MV", "NL", "PH", "SC", "SD", "US",
        // Island groups, which take the article in the plural
        "BQ", "CC", "CK", "FK", "FO", "KY", "MH", "SB", "TC", "UM", "VG", "VI"
    };

    private static string AddThePrefix(string country, string countryCode2)
    {
        // Lowercase: the placeholder is used mid-sentence ("connected from the ...").
        return ArticleCountryCodes.Contains(countryCode2)
            ? $"the {country}"
            : country;
    }

    private static string GetDisconnectReason(int reasonCode)
    {
        return reasonCode switch
        {
            1 => "Server shutting down",
            2 => "Disconnected",
            3 => "Disconnected from server",
            4 => "Connection lost",
            5 => "Overflow",
            6 => "Banned SteamID",
            7 => "SteamID already in use",
            8 => "Invalid Steam ticket",
            9 => "No Steam logon",
            10 or 11 or 12 => "Steam authorization failed",
            13 => "VAC ban",
            14 => "Account used in another location",
            15 => "VAC timed out",
            16 => "Not connected to Steam",
            17 => "Does not own the game",
            18 => "Info data overflow",
            23 or 24 or 26 => "Buffer overflow",
            29 or 72 => "Could not connect to server",
            30 or 84 or 85 => "Disconnected",
            39 => "Kicked by server",
            40 => "Added to ban list",
            41 => "Kicked and banned",
            43 or 44 => "Pure server: file mismatch",
            46 => "Connection rejected by game",
            49 or 134 => "Incorrect password",
            50 => "Direct connect reservation",
            51 => "Connection failed",
            52 => "Internal network failure",
            53 => "Reconnecting",
            54 or 55 => "Server state changed",
            56 => "Match ended",
            57 => "Loading map",
            58 => "Could not create server",
            59 => "Quit game",
            60 => "Host inactive",
            61 => "HLTV relay",
            62 => "Client consistency failure",
            63 => "Map verification failed",
            64 => "Required map missing",
            65 => "Map version mismatch",
            66 => "Server requires Steam",
            67 => "Steam denied connection",
            68 => "Connection denied by VAC",
            69 => "Server shutting down",
            73 => "Incompatible server version",
            74 or 77 or 79 or 80 or 81 => "Timed out",
            75 => "Server lost relay connection",
            76 => "Could not download network configuration",
            128 => "Bad challenge",
            135 => "Server full",
            141 => "Client out of date",
            142 => "Server out of date",
            146 => "Steam rejected connection",
            149 => "Not allowed on this server",
            157 => "Vote kicked",
            158 => "AFK",
            160 => "No logon",
            161 => "Game authentication failed",
            162 => "Input automation detected",
            163 => "VAC detected irregular gameplay",
            _ => "Disconnected"
        };
    }

    // Disconnects that are not real departures: the client is coming straight back.
    // Besides map/server transitions this covers the engine loop reconnects that
    // MultiAddonManager (and any forced workshop download) triggers, where a client
    // is dropped and reconnects once per addon. Announcing those spams the chat.
    //   1  NETWORK_DISCONNECT_SHUTDOWN
    //   53 NETWORK_DISCONNECT_RECONNECTION
    //   54 NETWORK_DISCONNECT_LOOPSHUTDOWN
    //   55 NETWORK_DISCONNECT_LOOPDEACTIVATE
    //   56 NETWORK_DISCONNECT_HOST_ENDGAME
    //   57 NETWORK_DISCONNECT_LOOP_LEVELLOAD_ACTIVATE
    //   69 NETWORK_DISCONNECT_SERVER_SHUTDOWN
    private static bool IsTransientDisconnectReason(int reasonCode)
    {
        return reasonCode is 1 or 53 or 54 or 55 or 56 or 57 or 69;
    }

    // Lenient on read: this is a file the README tells admins to hand-edit, and a stray
    // trailing comma should not take the plugin down.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

public sealed record ConnectAnnounceConfig
{
    public bool ShowEnhancedConnectMessage { get; init; } = true;
    public bool ShowEnhancedDisconnectMessage { get; init; } = true;
    public bool ShowEnhancedToAdmins { get; init; } = true;

    // "Subject" (default) or "Recipient". See the README; anything else is warned about and
    // treated as "Subject", which is the behaviour every existing install already has.
    public string AdminMessageMode { get; init; } = "Subject";
    public string AdminFlag { get; init; } = "@css/generic";
    public bool ShowStandardConnectMessage { get; init; }
    public bool ShowStandardDisconnectMessage { get; init; }
    public string GeoLiteDatabasePath { get; init; } = "GeoLite2-City.mmdb";
    public string PlayerNameColor { get; init; } = "Purple";
    public string SteamIdColor { get; init; } = "Default";
    public string LocationColor { get; init; } = "Green";

    // Optional overrides. Empty means "use LocationColor", so an existing config that only
    // sets LocationColor keeps colouring all five location placeholders the same way.
    public string CountryColor { get; init; } = "";
    public string CityColor { get; init; } = "";
    public string RegionColor { get; init; } = "";
    public string PlayerIpColor { get; init; } = "Default";
    public string PlayerTypeColor { get; init; } = "Default";
    public string DisconnectReasonLabelColor { get; init; } = "Green";
    public string DisconnectReasonColor { get; init; } = "Green";
}

internal sealed record GeoLocation(string City, string Region, string Country, string CountryCode2, string CountryCode3)
{
    public static GeoLocation Lan { get; } = new("an IP Address", "a Local Subnet", "a Local Area Network", "LN", "LAN");
    public static GeoLocation Unknown { get; } = new("Somewhere", "an Unknown Region", "an Unknown Country", "??", "???");
}

internal sealed class KeyValuesNode(string name)
{
    public string Name { get; } = name;
    public string? Value { get; set; }
    public Dictionary<string, KeyValuesNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KeyValuesNode GetOrAddChild(string key)
    {
        if (!Children.TryGetValue(key, out var child))
        {
            child = new KeyValuesNode(key);
            Children[key] = child;
        }

        return child;
    }

    public void SetValue(string key, string value)
    {
        GetOrAddChild(key).Value = value;
    }
}

internal static class KeyValuesParser
{
    public static KeyValuesNode Parse(string text)
    {
        var tokens = Tokenize(text).ToList();
        if (tokens.Count == 0)
        {
            return new KeyValuesNode("Root");
        }

        var index = 0;
        var rootName = tokens[index++];
        var root = new KeyValuesNode(rootName);
        if (index < tokens.Count && tokens[index] == "{")
        {
            index++;
            ParseChildren(root, tokens, ref index);
        }

        return root;
    }

    // A malformed file must not be able to take the server down. Without a depth limit,
    // deeply nested braces recurse until the stack overflows, and a StackOverflowException
    // cannot be caught: the whole game server process dies with nothing in the log.
    private const int MaxDepth = 32;

    private static void ParseChildren(KeyValuesNode parent, IReadOnlyList<string> tokens, ref int index, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"KeyValues nesting is deeper than {MaxDepth} levels.");
        }

        while (index < tokens.Count)
        {
            var key = tokens[index++];
            if (key == "}")
            {
                return;
            }

            if (index >= tokens.Count)
            {
                parent.GetOrAddChild(key);
                return;
            }

            if (tokens[index] == "{")
            {
                index++;
                ParseChildren(parent.GetOrAddChild(key), tokens, ref index, depth + 1);
            }
            else
            {
                parent.SetValue(key, tokens[index++]);
            }
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (text[i] is '{' or '}')
            {
                yield return text[i++].ToString();
                continue;
            }

            if (text[i] == '"')
            {
                var builder = new StringBuilder();
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        builder.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        i++;
                        break;
                    }

                    builder.Append(text[i++]);
                }

                yield return builder.ToString();
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}') i++;
            yield return text[start..i];
        }
    }
}

internal static class KeyValuesSerializer
{
    public static string Serialize(KeyValuesNode root)
    {
        var builder = new StringBuilder();
        WriteNode(builder, root, 0);
        return builder.ToString();
    }

    private static void WriteNode(StringBuilder builder, KeyValuesNode node, int depth)
    {
        var indent = new string('\t', depth);
        builder.Append(indent).Append('"').Append(Escape(node.Name)).AppendLine("\"");
        builder.Append(indent).AppendLine("{");
        foreach (var child in node.Children.Values)
        {
            if (child.Children.Count > 0)
            {
                WriteNode(builder, child, depth + 1);
            }
            else
            {
                var childIndent = new string('\t', depth + 1);
                builder.Append(childIndent).Append('"').Append(Escape(child.Name)).Append("\"\t\t\"").Append(Escape(child.Value ?? "")).AppendLine("\"");
            }
        }
        builder.Append(indent).AppendLine("}");
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

internal static class ChatColorTagExtensions
{
    private static readonly Dictionary<string, char> ColorTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = ChatColors.Default,
        ["White"] = ChatColors.White,
        ["DarkRed"] = ChatColors.DarkRed,
        ["Green"] = ChatColors.Green,
        ["LightYellow"] = ChatColors.LightYellow,
        ["LightBlue"] = ChatColors.LightBlue,
        ["Olive"] = ChatColors.Olive,
        ["Lime"] = ChatColors.Lime,
        ["Red"] = ChatColors.Red,
        ["LightPurple"] = ChatColors.LightPurple,
        ["Purple"] = ChatColors.Purple,
        ["Grey"] = ChatColors.Grey,
        ["Yellow"] = ChatColors.Yellow,
        ["Gold"] = ChatColors.Gold,
        ["Silver"] = ChatColors.Silver,
        ["Blue"] = ChatColors.Blue,
        ["DarkBlue"] = ChatColors.DarkBlue,
        ["BlueGrey"] = ChatColors.BlueGrey,
        ["Magenta"] = ChatColors.Magenta,
        ["LightRed"] = ChatColors.LightRed,
        ["Orange"] = ChatColors.Orange
    };

    public static string ReplaceColorTags(this string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        foreach (var (tag, color) in ColorTags)
        {
            input = input.Replace("{" + tag + "}", color.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return input;
    }

    public static IEnumerable<string> AvailableColorNames => ColorTags.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownColor(string colorName)
    {
        return !string.IsNullOrWhiteSpace(colorName) && ColorTags.ContainsKey(colorName);
    }

    public static string Colorize(string colorName, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (!ColorTags.TryGetValue(colorName, out var color))
        {
            throw new InvalidOperationException($"Invalid CSSSharp chat color '{colorName}'. Valid colors: {string.Join(", ", AvailableColorNames)}");
        }

        return $"{color}{value}{ChatColors.Default}";
    }
}
