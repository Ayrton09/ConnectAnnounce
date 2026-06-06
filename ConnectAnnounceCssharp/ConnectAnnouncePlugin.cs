using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
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

[MinimumApiVersion(80)]
public sealed class ConnectAnnouncePlugin : BasePlugin
{
    private const string Version = "1.0.2";
    private const ulong SteamId64Base = 76561197960265728UL;

    private readonly object _fileLock = new();
    private readonly object _geoLock = new();
    private readonly HashSet<ulong> _announcedSteamIds = [];
    private ConnectAnnounceConfig _config = new();
    private KeyValuesNode _countryShow = new("CountryShow");
    private DatabaseReader? _geoReader;
    private bool _geoLoadAttempted;
    private bool _filesLoaded;
    private bool _mapChanging;
    private string _dataDirectory = "";
    private string _settingsPath = "";
    private string _configPath = "";
    private string _legacyConfigPath = "";

    public override string ModuleName => "Connect Announce";
    public override string ModuleVersion => Version;
    public override string ModuleAuthor => "Ayrton09";
    public override string ModuleDescription => string.Empty;

    public override void Load(bool hotReload)
    {
        _dataDirectory = Path.Combine(ModuleDirectory, "data");
        _settingsPath = Path.Combine(_dataDirectory, "cannounce_settings.txt");
        _configPath = Path.Combine(GetCounterStrikeSharpConfigsPath(), "plugins", "ConnectAnnounce", "ConnectAnnounceConfig.json");
        _legacyConfigPath = Path.Combine(Server.GameDirectory, "addons", "counterstrikesharp", "configs", "plugins", "ConnectAnnounce", "ConnectAnnounceConfig.json");

        try
        {
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
    }

    public override void Unload(bool hotReload)
    {
        _geoReader?.Dispose();
        _geoReader = null;
    }

    private void OnMapStart(string mapName)
    {
        AddTimer(15.0f, () => _mapChanging = false);
    }

    private void OnMapEnd()
    {
        _mapChanging = true;
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
        if (!_filesLoaded)
        {
            return;
        }

        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (!IsConnectingPlayer(player))
        {
            return;
        }

        if (!_announcedSteamIds.Add(steamId.SteamId64))
        {
            return;
        }

        var steamKey = GetSteamKey(steamId);
        if (_config.ShowEnhancedConnectMessage)
        {
            var playerMsg = GetCountryMessage("messages", "playerjoin");
            var adminMsg = GetCountryMessage("messages_admin", "playerjoin");
            PrintEnhancedMessage(player!, playerMsg, adminMsg, steamKey);
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

        var player = @event.Userid;
        if (!IsRealPlayer(player))
        {
            return HookResult.Continue;
        }

        if (_mapChanging || IsMapTransitionDisconnectReason(@event.Reason))
        {
            return HookResult.Continue;
        }

        var steam64 = player!.AuthorizedSteamID?.SteamId64;
        if (steam64.HasValue)
        {
            _announcedSteamIds.Remove(steam64.Value);
        }

        if (_config.ShowEnhancedDisconnectMessage)
        {
            var reason = GetDisconnectReason(@event.Reason);
            var playerMsg = GetCountryMessage("messages", "playerdisc");
            var adminMsg = GetCountryMessage("messages_admin", "playerdisc");
            PrintEnhancedMessage(player!, playerMsg, adminMsg, disconnectReason: reason);
        }

        return HookResult.Continue;
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

        foreach (var target in FindTargets(caller, command.ArgByIndex(1)))
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
        command.ReplyToCommand(_filesLoaded
            ? "[CA] Reloaded Connect Announce configuration."
            : "[CA] Reload failed. Check the server log for the exact error.");
    }

    private void PrintEnhancedMessage(CCSPlayerController subject, string playerMessage, string adminMessage, string? steamKey = null, string? disconnectReason = null)
    {
        if (_config.ShowEnhancedToAdmins)
        {
            var formattedAdmin = FormatMessage(adminMessage, subject, steamKey, disconnectReason);
            var formattedPublic = FormatMessage(playerMessage, subject, steamKey, disconnectReason);
            foreach (var player in GetRealPlayers())
            {
                player.PrintToChat(" " + (HasPermission(player, "@css/generic") ? formattedAdmin : formattedPublic));
            }
        }
        else
        {
            var formatted = FormatMessage(playerMessage, subject, steamKey, disconnectReason);
            foreach (var player in GetRealPlayers())
            {
                player.PrintToChat(" " + formatted);
            }
        }
    }

    private string FormatMessage(string rawMessage, CCSPlayerController? player, string? steamKeyOverride = null, string? disconnectReason = null)
    {
        var message = ApplyColorTags(rawMessage);
        if (player == null)
        {
            return message;
        }

        var ip = ExtractIp(player.IpAddress);
        var location = LookupLocation(player);
        var steamKey = steamKeyOverride ?? GetSteamKey(player) ?? "";
        var playerType = HasPermission(player, "@css/generic") ? "Admin" : "Player";

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{PLAYERNAME}"] = ChatColorTagExtensions.Colorize(_config.PlayerNameColor, player.PlayerName),
            ["{STEAMID}"] = ChatColorTagExtensions.Colorize(_config.SteamIdColor, steamKey),
            ["{PLAYERCOUNTRY}"] = ChatColorTagExtensions.Colorize(_config.LocationColor, AddThePrefix(location.Country)),
            ["{PLAYERCOUNTRYSHORT}"] = ChatColorTagExtensions.Colorize(_config.LocationColor, location.CountryCode2),
            ["{PLAYERCOUNTRYSHORT3}"] = ChatColorTagExtensions.Colorize(_config.LocationColor, location.CountryCode3),
            ["{PLAYERCITY}"] = ChatColorTagExtensions.Colorize(_config.LocationColor, location.City),
            ["{PLAYERREGION}"] = ChatColorTagExtensions.Colorize(_config.LocationColor, location.Region),
            ["{PLAYERIP}"] = ChatColorTagExtensions.Colorize(_config.PlayerIpColor, ip),
            ["{PLAYERTYPE}"] = ChatColorTagExtensions.Colorize(_config.PlayerTypeColor, playerType),
            ["{DISC_REASON_LABEL}"] = ChatColorTagExtensions.Colorize(_config.DisconnectReasonLabelColor, "reason: "),
            ["{DISC_REASON}"] = ChatColorTagExtensions.Colorize(_config.DisconnectReasonColor, disconnectReason ?? "")
        };

        foreach (var replacement in replacements)
        {
            message = message.Replace(replacement.Key, replacement.Value, StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }

    private GeoLocation LookupLocation(CCSPlayerController player)
    {
        var ipText = ExtractIp(player.IpAddress);
        if (!IPAddress.TryParse(ipText, out var address))
        {
            return GeoLocation.Unknown;
        }

        if (IsLanIp(address))
        {
            return GeoLocation.Lan;
        }

        if (_geoReader == null)
        {
            return GeoLocation.Unknown;
        }

        try
        {
            var city = _geoReader.City(address);
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
    }

    private void LoadAllFiles()
    {
        lock (_fileLock)
        {
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
                _filesLoaded = false;
                Logger.LogError(ex, "Connect Announce could not load its files.");
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
            var mmdb = Path.IsPathRooted(_config.GeoLiteDatabasePath)
                ? _config.GeoLiteDatabasePath
                : Path.Combine(ModuleDirectory, _config.GeoLiteDatabasePath);
            if (!File.Exists(mmdb))
            {
                Logger.LogWarning("GeoLite2 database was not found at {Path}. GeoIP placeholders will use unknown values.", mmdb);
                return;
            }

            try
            {
                _geoReader = new DatabaseReader(mmdb, FileAccessMode.Memory);
                Logger.LogInformation("Loaded GeoLite2 database from {Path}", mmdb);
            }
            catch (Exception ex)
            {
                _geoReader?.Dispose();
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
        MigrateLegacyConfig();

        if (!File.Exists(_configPath))
        {
            _config = new ConnectAnnounceConfig();
            ValidateConfigColors();
            WriteConfig();
            return;
        }

        var json = File.ReadAllText(_configPath);
        _config = JsonSerializer.Deserialize<ConnectAnnounceConfig>(json, JsonOptions) ?? new ConnectAnnounceConfig();
        ValidateConfigColors();
        WriteConfig();
    }

    private void WriteConfig()
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions));
    }

    private void MigrateLegacyConfig()
    {
        if (string.IsNullOrWhiteSpace(_legacyConfigPath) ||
            string.Equals(_legacyConfigPath, _configPath, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(_configPath) ||
            !File.Exists(_legacyConfigPath))
        {
            return;
        }

        File.Copy(_legacyConfigPath, _configPath);
        Logger.LogInformation("Migrated Connect Announce configuration from legacy path {LegacyPath} to {ConfigPath}", _legacyConfigPath, _configPath);
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

    private void ValidateConfigColors()
    {
        var configuredColors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(_config.PlayerNameColor)] = _config.PlayerNameColor,
            [nameof(_config.SteamIdColor)] = _config.SteamIdColor,
            [nameof(_config.LocationColor)] = _config.LocationColor,
            [nameof(_config.PlayerIpColor)] = _config.PlayerIpColor,
            [nameof(_config.PlayerTypeColor)] = _config.PlayerTypeColor,
            [nameof(_config.DisconnectReasonLabelColor)] = _config.DisconnectReasonLabelColor,
            [nameof(_config.DisconnectReasonColor)] = _config.DisconnectReasonColor
        };

        foreach (var (name, color) in configuredColors)
        {
            if (!ChatColorTagExtensions.IsKnownColor(color))
            {
                throw new InvalidOperationException($"{name} has invalid CSSSharp chat color '{color}'. Valid colors: {string.Join(", ", ChatColorTagExtensions.AvailableColorNames)}");
            }
        }
    }

    private void LoadCountryMessages()
    {
        if (!File.Exists(_settingsPath))
        {
            _countryShow = DefaultCountryShow();
            File.WriteAllText(_settingsPath, KeyValuesSerializer.Serialize(_countryShow));
            return;
        }

        _countryShow = KeyValuesParser.Parse(File.ReadAllText(_settingsPath));
        EnsureDefaultCountryMessages();
        File.WriteAllText(_settingsPath, KeyValuesSerializer.Serialize(_countryShow));
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
        foreach (var sectionName in new[] { "messages", "messages_admin" })
        {
            var section = root.GetOrAddChild(sectionName);
            section.SetValue("playerjoin", "{PLAYERNAME} connected from {PLAYERCOUNTRY} | {PLAYERCITY}");
            section.SetValue("playerdisc", "{PLAYERNAME} ({STEAMID}) disconnected! {DISC_REASON_LABEL}{DISC_REASON}");
        }

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

    private IEnumerable<CCSPlayerController> GetRealPlayers()
    {
        return Utilities.GetPlayers().Where(IsRealPlayer)!;
    }

    private static bool IsRealPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsBot: false, UserId: not null };
    }

    private static bool IsConnectingPlayer(CCSPlayerController? player)
    {
        return player is { IsValid: true, IsBot: false };
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

    private static string AddThePrefix(string country)
    {
        var prefixes = new[] { "United", "Republic", "Federation", "Island", "Netherlands", "Isle", "Bahamas", "Maldives", "Philippines", "Vatican" };
        return prefixes.Any(country.Contains)
            ? $"The {country}"
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

    private static bool IsMapTransitionDisconnectReason(int reasonCode)
    {
        return reasonCode is 1 or 56 or 69;
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record ConnectAnnounceConfig
{
    public bool ShowEnhancedConnectMessage { get; init; } = true;
    public bool ShowEnhancedDisconnectMessage { get; init; } = true;
    public bool ShowEnhancedToAdmins { get; init; }
    public bool ShowStandardConnectMessage { get; init; }
    public bool ShowStandardDisconnectMessage { get; init; }
    public string GeoLiteDatabasePath { get; init; } = "GeoLite2-City.mmdb";
    public string PlayerNameColor { get; init; } = "Purple";
    public string SteamIdColor { get; init; } = "Default";
    public string LocationColor { get; init; } = "Green";
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

    private static void ParseChildren(KeyValuesNode parent, IReadOnlyList<string> tokens, ref int index)
    {
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
                ParseChildren(parent.GetOrAddChild(key), tokens, ref index);
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
