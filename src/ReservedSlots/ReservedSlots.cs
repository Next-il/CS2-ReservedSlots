using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using System.Drawing;

namespace ReservedSlots;

public class ReservedSlotsConfig : BasePluginConfig
{
    [JsonPropertyName("Reserved Flags")] public List<string> reservedFlags { get; set; } = new() { "@css/reservation", "@css/vip" };
    [JsonPropertyName("Admin Reserved Flags")] public List<string> adminFlags { get; set; } = new() { "@css/ban", "@css/admin" };
    [JsonPropertyName("Reserved Player Kick Cooldown")] public int reservedPlayerKickCooldown { get; set; } = 30;
    [JsonPropertyName("Reserved Slots")] public int reservedSlots { get; set; } = 1;
    [JsonPropertyName("Reserved Slots Method")] public int reservedSlotsMethod { get; set; } = 0;
    [JsonPropertyName("Leave One Slot Open")] public bool openSlot { get; set; } = false;
    [JsonPropertyName("Kick Immunity Type")] public int kickImmunity { get; set; } = 0;
    [JsonPropertyName("Kick Reason")] public int kickReason { get; set; } = 135;
    [JsonPropertyName("Kick Delay")] public int kickDelay { get; set; } = 5;
    [JsonPropertyName("Joining Player Kick Delay")] public int joiningPlayerKickDelay { get; set; } = -1;
    [JsonPropertyName("In-Server Player Kick Delay")] public int inServerPlayerKickDelay { get; set; } = -1;
    [JsonPropertyName("Kick Check Method")] public int kickCheckMethod { get; set; } = 0;
    [JsonPropertyName("Kick Type")] public int kickType { get; set; } = 0;
    [JsonPropertyName("Kick Players In Spectate")] public bool kickPlayersInSpectate { get; set; } = true;
    [JsonPropertyName("Log Kicked Players")] public bool logKickedPlayers { get; set; } = true;
    [JsonPropertyName("Display Kicked Players Message")] public int displayKickedPlayers { get; set; } = 2;
	[JsonPropertyName("Auto Update sv_visiblemaxplayers")] public bool autoUpdateVisibleMaxPlayers { get; set; } = true;

}

public class ReservedSlots : BasePlugin, IPluginConfig<ReservedSlotsConfig>
{
    private readonly record struct PendingKickState(KickReason Reason, long Token);

    public override string ModuleName => "Reserved Slots";
    public override string ModuleAuthor => "Nocky (SourceFactory.eu)";
    public override string ModuleVersion => "1.2.1";

    // Flags are only readable once Steam has validated a player (AdminManager keys on AuthorizedSteamID).
    // A "server is full" kick for a player Steam has not validated yet waits up to this long, then decides again.
    private const int AuthGraceSeconds = 15;

    public enum KickType
    {
        Random,
        HighestPing,
        HighestScore,
        LowestScore,
        HighestTime,
    }

    public enum KickReason
    {
        ServerIsFull,
        ReservedPlayerJoined,
    }

    public enum ReservedType
    {
        VIP,
        Admin,
        None
    }

    private HashSet<ulong> waitingForSelectTeam = new();

    private HashSet<ulong> reservedPlayers = new();
    private HashSet<ulong> immunePlayers = new();
    private Dictionary<ulong, PendingKickState> waitingForKick = new();
    public ReservedSlotsConfig Config { get; set; } = new();
    private HashSet<string> _normalizedReservedFlags = new(StringComparer.Ordinal);
    private HashSet<string> _normalizedAdminFlags = new(StringComparer.Ordinal);
    private HashSet<ulong> _playersWithInstructorEnabled = new();
    private Dictionary<ulong, DateTime> _reservedKickCooldownExpirations = new();
    private long _pendingKickSequence;
    private int? _cachedVisibleMaxPlayers;

    public void OnConfigParsed(ReservedSlotsConfig config)
    {
        Config = config;
        _normalizedReservedFlags = NormalizeFlags(Config.reservedFlags);
        _normalizedAdminFlags = NormalizeFlags(Config.adminFlags);

        if (!Config.reservedFlags.Any())
            SendConsoleMessage("[Reserved Slots] Reserved Flags and Roles cannot be empty!", ConsoleColor.Red);

        if (Config.reservedPlayerKickCooldown < 0)
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Reserved Player Kick Cooldown' value {Value}, falling back to 30 seconds.", Config.reservedPlayerKickCooldown);
            Config.reservedPlayerKickCooldown = 30;
        }

        if (!Enum.IsDefined(typeof(NetworkDisconnectionReason), Config.kickReason))
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Kick Reason' value {Value}, falling back to 135 (NETWORK_DISCONNECT_KICKED_RESERVEDSLOT).", Config.kickReason);
            Config.kickReason = 135;
        }

        if (Config.reservedSlotsMethod < 0 || Config.reservedSlotsMethod > 3)
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Reserved Slots Method' value {Value} (valid: 0-3), falling back to 0.", Config.reservedSlotsMethod);
            Config.reservedSlotsMethod = 0;
        }

        if (Config.kickImmunity < 0 || Config.kickImmunity > 2)
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Kick Immunity Type' value {Value} (valid: 0-2), falling back to 0.", Config.kickImmunity);
            Config.kickImmunity = 0;
        }

        if (!Enum.IsDefined(typeof(KickType), Config.kickType))
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Kick Type' value {Value} (valid: 0-4), falling back to 0 (Random).", Config.kickType);
            Config.kickType = 0;
        }

        if (Config.displayKickedPlayers < 0 || Config.displayKickedPlayers > 2)
        {
            Logger.LogWarning("[Reserved Slots] Invalid 'Display Kicked Players Message' value {Value} (valid: 0-2), falling back to 2.", Config.displayKickedPlayers);
            Config.displayKickedPlayers = 2;
        }
    }

	public void UpdateMaxPlayers()
	{
		// Update sv_visiblemaxplayers to match the max players minus reserved slots
        var visibleMaxPlayers = ConVar.Find("sv_visiblemaxplayers");
        if (visibleMaxPlayers != null)
        {
            visibleMaxPlayers.SetValue(Server.MaxPlayers - Config.reservedSlots);
            RefreshVisibleMaxPlayers();
            Logger.LogInformation("[Reserved Slots] sv_visiblemaxplayers has been updated to {Value} based on the reserved slots configuration.", GetVisibleMaxPlayers());
        }
        else
        {
            Logger.LogWarning("[Reserved Slots] Could not find convar 'sv_visiblemaxplayers' to update its value based on the reserved slots configuration.");
        }
	}

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            PruneExpiredReservedKickCooldowns();
            ResetTrackedGameInstructorStates();
            ResetTransientPlayerState();

            AddTimer(3.0f, () =>
            {
                RefreshVisibleMaxPlayers();

				if (Config.autoUpdateVisibleMaxPlayers)
				{
					UpdateMaxPlayers();
				}

                // ResetTransientPlayerState above dropped everyone's immunity; players already validated by Steam get it back here
                RebuildReservedPlayersCache();
            }, TimerFlags.STOP_ON_MAPCHANGE);
        });

        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);

        RefreshVisibleMaxPlayers();

        if (hotReload)
            RebuildReservedPlayersCache();
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_cachedVisibleMaxPlayers == null)
            RefreshVisibleMaxPlayers();
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && TryConsumeWaitingForTeamSelection(player.SteamID))
        {
            TryPerformReservedPlayerKick(player, GetPlayersReservedType(player));
        }
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
        {
            ClearTrackedPlayerState(player.SteamID);
            SetGameInstructorState(player, enabled: false);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && IsHumanPlayer(player))
        {
            if (_normalizedAdminFlags.Count == 0 && _normalizedReservedFlags.Count == 0)
                return HookResult.Continue;

            ClearPendingAdmissionState(player.SteamID);

            var playerReservedType = GetPlayersReservedType(player);
            UpdateReservationTracking(player, playerReservedType);

            HandleAdmission(player, playerReservedType, GetConnectedHumanPlayers());
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// Steam validated a player. Until now their flags were unreadable, so a connect-time decision may have
    /// treated an admin/VIP as a regular player: give them their immunity, and if a "server is full" kick is
    /// waiting for them, cancel it and admit them the way a reserved join would have been.
    /// </summary>
    private void OnClientAuthorized(int playerSlot, SteamID steamId)
    {
        var player = Utilities.GetPlayerFromSlot(playerSlot);
        // Still connecting: their connect-full event comes later and sees the flags then
        if (player == null || !IsHumanPlayer(player))
            return;

        var reservedType = GetReservedType(AdminManager.GetPlayerAdminData(steamId));
        UpdateReservationTracking(player, reservedType);

        if (reservedType == ReservedType.None)
            return;

        if (!waitingForKick.TryGetValue(player.SteamID, out var pendingKick) || pendingKick.Reason != KickReason.ServerIsFull)
            return;

        ClearPendingKick(player.SteamID, pendingKick);
        Logger.LogInformation("[Reserved Slots] {Name} ({SteamID}) was validated by Steam after connecting and holds a reservation - the server-full kick is cancelled.", player.PlayerName, player.SteamID);
        HandleAdmission(player, reservedType, GetConnectedHumanPlayers());
    }

    private void HandleAdmission(CCSPlayerController player, ReservedType playerReservedType, List<CCSPlayerController> connectedHumanPlayers)
    {
        {
            int maxPlayers = Server.MaxPlayers;
            int totalPlayers = GetPlayersCount(connectedHumanPlayers);

            switch (Config.reservedSlotsMethod)
            {
                case 1:
                    if (totalPlayers > maxPlayers - Config.reservedSlots)
                    {
                        if (playerReservedType == ReservedType.VIP)
                        {
                            if ((Config.openSlot && totalPlayers >= maxPlayers) || (!Config.openSlot && totalPlayers > maxPlayers))
                                PerformKickCheckMethod(player, connectedHumanPlayers);
                        }
                        else if (playerReservedType == ReservedType.None)
                            PerformKick(player, KickReason.ServerIsFull);
                    }
                    break;

                case 2:
                    if (totalPlayers - GetPlayersCountWithReservationFlag(connectedHumanPlayers) > maxPlayers - Config.reservedSlots)
                    {
                        if (playerReservedType == ReservedType.VIP)
                        {
                            if ((Config.openSlot && totalPlayers >= maxPlayers) || (!Config.openSlot && totalPlayers > maxPlayers))
                                PerformKickCheckMethod(player, connectedHumanPlayers);
                        }
                        else if (playerReservedType == ReservedType.None)
                            PerformKick(player, KickReason.ServerIsFull);
                    }
                    break;

                case 3:
                    HandleOverflowModeJoin(player, playerReservedType, maxPlayers, connectedHumanPlayers);
                    break;

                default:
                    if (totalPlayers >= maxPlayers)
                    {
                        if (playerReservedType == ReservedType.VIP)
                            PerformKickCheckMethod(player, connectedHumanPlayers);
                        else if (playerReservedType == ReservedType.None)
                            PerformKick(player, KickReason.ServerIsFull);
                    }
                    break;
            }
        }
    }

    public ReservedType GetPlayersReservedType(CCSPlayerController player)
    {
        return GetReservedType(AdminManager.GetPlayerAdminData(player));
    }

    private ReservedType GetReservedType(AdminData? adminData)
    {
        if (adminData == null)
            return ReservedType.None;

        var playerFlags = adminData.GetAllFlags();
        if (!playerFlags.Any())
            return ReservedType.None;

        if (_normalizedAdminFlags.Count > 0)
        {
            if (playerFlags.Any(flag => _normalizedAdminFlags.Contains(flag)))
                return ReservedType.Admin;
        }

        if (_normalizedReservedFlags.Count > 0)
        {
            if (playerFlags.Any(flag => _normalizedReservedFlags.Contains(flag)))
                return ReservedType.VIP;
        }

        return ReservedType.None;
    }

    private static HashSet<string> NormalizeFlags(IEnumerable<string> flags)
    {
        return flags
            .Where(item => !ulong.TryParse(item, out _))
            .ToHashSet(StringComparer.Ordinal);
    }

    private void ResetTransientPlayerState()
    {
        waitingForSelectTeam.Clear();
        waitingForKick.Clear();
        reservedPlayers.Clear();
        immunePlayers.Clear();
    }

    private void ClearPendingAdmissionState(ulong steamId)
    {
        waitingForSelectTeam.Remove(steamId);
        ClearPendingKick(steamId);
    }

    private void ClearReservationTracking(ulong steamId)
    {
        reservedPlayers.Remove(steamId);
        immunePlayers.Remove(steamId);
    }

    private void ClearTrackedPlayerState(ulong steamId)
    {
        ClearPendingAdmissionState(steamId);
        ClearReservationTracking(steamId);
    }

    private bool TryConsumeWaitingForTeamSelection(ulong steamId)
    {
        return waitingForSelectTeam.Remove(steamId);
    }

    private void TrackWaitingForTeamSelection(ulong steamId)
    {
        waitingForSelectTeam.Add(steamId);
    }

    private void UpdateReservationTracking(CCSPlayerController player, ReservedType type)
    {
        if (type == ReservedType.None)
        {
            ClearReservationTracking(player.SteamID);
            return;
        }

        bool isImmune = Config.kickImmunity switch
        {
            1 => type == ReservedType.Admin,
            2 => type == ReservedType.VIP,
            _ => type == ReservedType.Admin || type == ReservedType.VIP
        };

        reservedPlayers.Add(player.SteamID);

        if (isImmune)
            immunePlayers.Add(player.SteamID);
        else
            immunePlayers.Remove(player.SteamID);
    }

    private void RebuildReservedPlayersCache()
    {
        reservedPlayers.Clear();
        immunePlayers.Clear();

        foreach (var player in Utilities.GetPlayers().Where(IsHumanPlayer))
        {
            var playerReservedType = GetPlayersReservedType(player);
            UpdateReservationTracking(player, playerReservedType);
        }
    }

    public void PerformKickCheckMethod(CCSPlayerController player)
    {
        PerformKickCheckMethod(player, GetConnectedHumanPlayers());
    }

    private void PerformKickCheckMethod(CCSPlayerController player, IEnumerable<CCSPlayerController> connectedHumanPlayers)
    {
        var reservedType = GetPlayersReservedType(player);

        switch (Config.kickCheckMethod)
        {
            case 1:
                if (!CanTriggerReservedKick(player, reservedType))
                    return;

                TrackWaitingForTeamSelection(player.SteamID);
                break;
            default:
                TryPerformReservedPlayerKick(player, reservedType, connectedHumanPlayers);
                break;
        }
    }

    public void PerformKick(CCSPlayerController? player, KickReason reason)
    {
        if (player == null || !player.IsValid)
            return;

        var name = player.PlayerName;
        var steamid = player.SteamID.ToString();
        var playerSteamId = player.SteamID;
        int delay = GetKickDelay(reason);

        // Steam has not validated this player yet, so their flags cannot be read and "no reservation" is only a guess.
        // Never turn them away on that guess: go through the timer, which waits for Steam and decides again.
        bool awaitingSteam = reason == KickReason.ServerIsFull && player.AuthorizedSteamID == null;

        if (delay >= 1 || awaitingSteam)
        {
            if (!TryStartPendingKick(playerSteamId, reason, out var pendingKick))
                return;

            if (delay >= 1)
                ShowKickHint(player, reason, delay);

            ScheduleKick(playerSteamId, name, steamid, reason, pendingKick, Math.Max(delay, 1), DateTime.UtcNow.AddSeconds(AuthGraceSeconds));
        }
        else
        {
            Server.NextFrame(() =>
            {
                var deferredPlayer = FindHumanPlayer(playerSteamId);
                if (deferredPlayer == null || !ShouldStillKick(deferredPlayer, reason))
                    return;

                deferredPlayer.Disconnect((NetworkDisconnectionReason)Config.kickReason);
                LogMessage(name, steamid, reason);
            });
        }
    }

    private void ScheduleKick(ulong steamId, string name, string steamIdText, KickReason reason, PendingKickState pendingKick, float after, DateTime steamDeadline)
    {
        AddTimer(after, () =>
        {
            if (!IsCurrentPendingKick(steamId, pendingKick))
                return;

            var target = FindHumanPlayer(steamId);
            if (target == null)
            {
                ClearPendingKick(steamId, pendingKick);
                return;
            }

            // Still not validated by Steam: give it a moment longer, then decide with whatever is known
            if (reason == KickReason.ServerIsFull && target.AuthorizedSteamID == null && DateTime.UtcNow < steamDeadline)
            {
                ScheduleKick(steamId, name, steamIdText, reason, pendingKick, 1f, steamDeadline);
                return;
            }

            if (!ShouldStillKick(target, reason))
            {
                ClearPendingKick(steamId, pendingKick);

                if (reason == KickReason.ServerIsFull)
                {
                    // Validated late and holds a reservation: admit them the way a reserved join would have been
                    var reservedType = GetPlayersReservedType(target);
                    UpdateReservationTracking(target, reservedType);
                    HandleAdmission(target, reservedType, GetConnectedHumanPlayers());
                }
                return;
            }

            target.Disconnect((NetworkDisconnectionReason)Config.kickReason);
            LogMessage(name, steamIdText, reason);
            ClearPendingKick(steamId, pendingKick);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// The last check before a kick goes out. A "server is full" kick is dropped when the player turns out to hold a
    /// reservation; a kick that makes room for a reserved player is dropped when its victim has become immune since
    /// (an admin Steam validated after they were picked).
    /// </summary>
    private bool ShouldStillKick(CCSPlayerController player, KickReason reason)
    {
        if (reason == KickReason.ServerIsFull)
        {
            if (GetPlayersReservedType(player) == ReservedType.None)
                return true;

            Logger.LogInformation("[Reserved Slots] {Name} ({SteamID}) holds a reservation after all - not kicking them as server full.", player.PlayerName, player.SteamID);
            return false;
        }

        if (!immunePlayers.Contains(player.SteamID))
            return true;

        Logger.LogInformation("[Reserved Slots] {Name} ({SteamID}) became immune before the kick - skipping it.", player.PlayerName, player.SteamID);
        return false;
    }

    private static CCSPlayerController? FindHumanPlayer(ulong steamId)
    {
        return Utilities.GetPlayers().FirstOrDefault(p => IsHumanPlayer(p) && p.SteamID == steamId);
    }

    private int GetKickDelay(KickReason reason)
    {
        return reason switch
        {
            KickReason.ServerIsFull when Config.joiningPlayerKickDelay >= 0 => Config.joiningPlayerKickDelay,
            KickReason.ReservedPlayerJoined when Config.inServerPlayerKickDelay >= 0 => Config.inServerPlayerKickDelay,
            _ => Config.kickDelay
        };
    }

    public void LogMessage(string name, string steamid, KickReason reason)
    {
        switch (reason)
        {
            case KickReason.ServerIsFull:
                if (Config.logKickedPlayers)
                    Logger.LogInformation($"Player {name} ({steamid}) was kicked, because the server is full.");

                if (Config.displayKickedPlayers == 1)
                    Server.PrintToChatAll(Localizer["Chat.PlayerWasKicked.ServerIsFull", name]);
                else if (Config.displayKickedPlayers == 2)
                {
                    foreach (var admin in Utilities.GetPlayers().Where(p => AdminManager.PlayerHasPermissions(p, "@css/generic")))
                    {
                        admin.PrintToChat(Localizer["Chat.PlayerWasKicked.ServerIsFull", name]);
                    }
                }
                break;

            case KickReason.ReservedPlayerJoined:
                if (Config.logKickedPlayers)
                    Logger.LogInformation($"Player {name} ({steamid}) was kicked, because player with a reservation slot joined.");

                if (Config.displayKickedPlayers == 1)
                    Server.PrintToChatAll(Localizer["Chat.PlayerWasKicked.ReservedPlayerJoined", name]);
                else if (Config.displayKickedPlayers == 2)
                {
                    foreach (var admin in Utilities.GetPlayers().Where(p => AdminManager.PlayerHasPermissions(p, "@css/generic")))
                    {
                        admin.PrintToChat(Localizer["Chat.PlayerWasKicked.ReservedPlayerJoined", name]);
                    }
                }
                break;
        }
    }

    private void HandleOverflowModeJoin(CCSPlayerController player, ReservedType playerReservedType, int maxPlayers, IReadOnlyCollection<CCSPlayerController> connectedHumanPlayers)
    {
        int publicSlots = GetPublicSlots(maxPlayers);
        int joinThreshold = Config.openSlot ? publicSlots - 1 : publicSlots;
        if (joinThreshold < 0)
            joinThreshold = 0;

        int totalPlayers = GetPlayersCount(connectedHumanPlayers);

        if (totalPlayers <= joinThreshold)
            return;

        if (playerReservedType == ReservedType.VIP || playerReservedType == ReservedType.Admin)
        {
            PerformKickCheckMethod(player, connectedHumanPlayers);
            return;
        }

        PerformKick(player, KickReason.ServerIsFull);
    }

    private bool TryPerformReservedPlayerKick(CCSPlayerController player, ReservedType reservedType, IEnumerable<CCSPlayerController>? connectedHumanPlayers = null, bool denyJoinOnCooldown = true)
    {
        if (!CanTriggerReservedKick(player, reservedType, denyJoinOnCooldown))
            return false;

        var kickedPlayer = connectedHumanPlayers == null
            ? getPlayerToKick(player)
            : getPlayerToKick(player, connectedHumanPlayers);

        if (kickedPlayer == null)
        {
            Logger.LogWarning("[Reserved Slots] Selected player is NULL, no one is kicked!");
            return false;
        }

        RecordReservedKickCooldown(player, reservedType);
        PerformKick(kickedPlayer, KickReason.ReservedPlayerJoined);
        return true;
    }

    private bool TryStartPendingKick(ulong steamId, KickReason reason, out PendingKickState pendingKick)
    {
        if (waitingForKick.ContainsKey(steamId))
        {
            pendingKick = default;
            return false;
        }

        pendingKick = new PendingKickState(reason, ++_pendingKickSequence);
        waitingForKick[steamId] = pendingKick;
        return true;
    }

    private bool IsCurrentPendingKick(ulong steamId, PendingKickState pendingKick)
    {
        return waitingForKick.TryGetValue(steamId, out var currentPendingKick) && currentPendingKick == pendingKick;
    }

    private void ClearPendingKick(ulong steamId, PendingKickState? expectedPendingKick = null)
    {
        if (!waitingForKick.TryGetValue(steamId, out var currentPendingKick))
            return;

        if (expectedPendingKick.HasValue && currentPendingKick != expectedPendingKick.Value)
            return;

        waitingForKick.Remove(steamId);
    }

    private bool CanTriggerReservedKick(CCSPlayerController player, ReservedType reservedType, bool denyJoinOnCooldown = true)
    {
        if (reservedType == ReservedType.None)
        {
            Logger.LogWarning("[Reserved Slots] Player {Name} ({SteamID}) attempted reserved-slot kick without a reserved type.", player.PlayerName, player.SteamID);
            return false;
        }

        if (reservedType != ReservedType.VIP)
            return true;

        if (!TryGetReservedKickCooldownRemaining(player.SteamID, out int remainingSeconds))
            return true;

        Logger.LogInformation("[Reserved Slots] Player {Name} ({SteamID}) with Reserved Flags cannot kick another player for {RemainingSeconds} more second(s).", player.PlayerName, player.SteamID, remainingSeconds);

        if (denyJoinOnCooldown)
            PerformKick(player, KickReason.ServerIsFull);

        return false;
    }

    private void RecordReservedKickCooldown(CCSPlayerController player, ReservedType reservedType)
    {
        if (reservedType != ReservedType.VIP || Config.reservedPlayerKickCooldown <= 0)
            return;

        _reservedKickCooldownExpirations[player.SteamID] = DateTime.UtcNow.AddSeconds(Config.reservedPlayerKickCooldown);
    }

    private bool TryGetReservedKickCooldownRemaining(ulong steamId, out int remainingSeconds)
    {
        remainingSeconds = 0;

        if (Config.reservedPlayerKickCooldown <= 0)
            return false;

        PruneExpiredReservedKickCooldowns();

        if (!_reservedKickCooldownExpirations.TryGetValue(steamId, out var expiration))
            return false;

        var remaining = expiration - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _reservedKickCooldownExpirations.Remove(steamId);
            return false;
        }

        remainingSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return true;
    }

    private void PruneExpiredReservedKickCooldowns()
    {
        if (_reservedKickCooldownExpirations.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var expiredSteamIds = _reservedKickCooldownExpirations
            .Where(entry => entry.Value <= now)
            .Select(entry => entry.Key)
            .ToList();

        foreach (var steamId in expiredSteamIds)
        {
            _reservedKickCooldownExpirations.Remove(steamId);
        }
    }

    private int GetPublicSlots(int maxPlayers)
    {
        var visibleMaxPlayers = GetVisibleMaxPlayers();
        if (visibleMaxPlayers.HasValue)
            return Math.Max(0, visibleMaxPlayers.Value);

        int publicSlots = maxPlayers - Config.reservedSlots;
        return publicSlots < 0 ? 0 : publicSlots;
    }

    private void RefreshVisibleMaxPlayers()
    {
        var cvar = ConVar.Find("sv_visiblemaxplayers");
        if (cvar == null)
        {
            Logger.LogWarning("[Reserved Slots] Could not find convar 'sv_visiblemaxplayers'. Keeping previous cached value: {Value}", _cachedVisibleMaxPlayers);
            return;
        }

        var value = cvar.GetPrimitiveValue<int>();

        if (value < 0)
        {
            Logger.LogWarning("[Reserved Slots] sv_visiblemaxplayers returned {Value}. Keeping previous cached value: {CachedValue}", value, _cachedVisibleMaxPlayers);
            return;
        }

        _cachedVisibleMaxPlayers = value;
        Logger.LogInformation("[Reserved Slots] Cached sv_visiblemaxplayers value: {Value}", _cachedVisibleMaxPlayers);
    }

    private int? GetVisibleMaxPlayers()
    {
        return _cachedVisibleMaxPlayers;
    }

    private CCSPlayerController? getPlayerToKick(CCSPlayerController client)
    {
        return getPlayerToKick(client, GetConnectedHumanPlayers());
    }

    private CCSPlayerController? getPlayerToKick(CCSPlayerController client, IEnumerable<CCSPlayerController> connectedHumanPlayers)
    {
        CCSPlayerController? spectatorCandidate = null;
        CCSPlayerController? fallbackCandidate = null;
        int spectatorCandidatesSeen = 0;
        int fallbackCandidatesSeen = 0;

        foreach (var player in connectedHumanPlayers)
        {
            if (!player.PlayerPawn.IsValid || player == client || waitingForKick.ContainsKey(player.SteamID) || immunePlayers.Contains(player.SteamID))
                continue;

            if (Config.kickPlayersInSpectate && IsSpectatorCandidate(player))
                EvaluateKickCandidate(player, ref spectatorCandidate, ref spectatorCandidatesSeen);
            else
                EvaluateKickCandidate(player, ref fallbackCandidate, ref fallbackCandidatesSeen);
        }

        return spectatorCandidate ?? fallbackCandidate;
    }

    private void EvaluateKickCandidate(CCSPlayerController player, ref CCSPlayerController? currentCandidate, ref int candidatesSeen)
    {
        candidatesSeen++;

        switch (Config.kickType)
        {
            case (int)KickType.HighestPing:
                if (currentCandidate == null || player.Ping > currentCandidate.Ping)
                    currentCandidate = player;
                break;

            case (int)KickType.HighestScore:
                if (currentCandidate == null || player.Score > currentCandidate.Score)
                    currentCandidate = player;
                break;

            case (int)KickType.LowestScore:
                if (currentCandidate == null || player.Score < currentCandidate.Score)
                    currentCandidate = player;
                break;

            default:
                if (Random.Shared.Next(candidatesSeen) == 0)
                    currentCandidate = player;
                break;
        }
    }

    private static bool IsSpectatorCandidate(CCSPlayerController player)
    {
        return player.Team == CsTeam.None || player.Team == CsTeam.Spectator;
    }

    private static bool IsHumanPlayer(CCSPlayerController player)
    {
        return player.IsValid &&
            !player.IsHLTV &&
            !player.IsBot &&
            player.Connected == PlayerConnectedState.Connected &&
            player.SteamID.ToString().Length == 17;
    }

    private static List<CCSPlayerController> GetConnectedHumanPlayers()
    {
        return Utilities.GetPlayers().Where(IsHumanPlayer).ToList();
    }

    private static int GetPlayersCount()
    {
        return GetPlayersCount(GetConnectedHumanPlayers());
    }

    private static int GetPlayersCount(IReadOnlyCollection<CCSPlayerController> connectedHumanPlayers)
    {
        return connectedHumanPlayers.Count;
    }

    private int GetPlayersCountWithReservationFlag()
    {
        return GetPlayersCountWithReservationFlag(GetConnectedHumanPlayers());
    }

    private int GetPlayersCountWithReservationFlag(IEnumerable<CCSPlayerController> connectedHumanPlayers)
    {
        return connectedHumanPlayers.Count(p => reservedPlayers.Contains(p.SteamID));
    }

    private void ShowKickHint(CCSPlayerController player, KickReason reason, int delay)
    {
        if (!player.IsValid)
            return;

        var pawn = player.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid)
            return;

        var kickMessage = reason == KickReason.ServerIsFull
            ? Localizer["Hud.ServerIsFull"]
            : Localizer["Hud.ReservedPlayerJoined"];

        SetGameInstructorState(player, enabled: true);

        AddTimer(0.25f, () =>
        {
            if (!player.IsValid)
            {
                SetGameInstructorState(player, enabled: false);
                return;
            }

            var currentPawn = player.PlayerPawn?.Value;
            if (currentPawn == null || !currentPawn.IsValid)
            {
                SetGameInstructorState(player, enabled: false);
                return;
            }

            var hint = Utilities.CreateEntityByName<CEnvInstructorHint>("env_instructor_hint");
            if (hint == null)
            {
                SetGameInstructorState(player, enabled: false);
                return;
            }

            hint.Static = true;
            hint.Caption = kickMessage;
            hint.Timeout = delay;
            hint.Icon_Onscreen = "icon_alert";
            hint.Icon_Offscreen = "icon_arrow_up";
            hint.Binding = "";
            hint.Color = Color.FromArgb(255, 255, 100, 0);
            hint.Range = 0f;
            hint.NoOffscreen = true;
            hint.ForceCaption = true;

            hint.DispatchSpawn();
            hint.AcceptInput("ShowHint", currentPawn, currentPawn);
            hint.AddEntityIOEvent("Kill", null, null, string.Empty, delay + 0.25f, 0);

            AddTimer(delay + 0.25f, () =>
            {
                SetGameInstructorState(player, enabled: false);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ResetTrackedGameInstructorStates()
    {
        if (_playersWithInstructorEnabled.Count == 0)
            return;

        var hadTrackedPlayers = _playersWithInstructorEnabled.Count > 0;

        foreach (var player in Utilities.GetPlayers().Where(IsHumanPlayer).Where(player => _playersWithInstructorEnabled.Contains(player.SteamID)))
        {
            SetGameInstructorState(player, enabled: false);
        }

        _playersWithInstructorEnabled.Clear();

        if (hadTrackedPlayers)
            Server.ExecuteCommand("sv_gameinstructor_disable true");
    }

    private void SetGameInstructorState(CCSPlayerController? player, bool enabled)
    {
        if (player == null)
            return;

        if (!player.IsValid)
        {
            if (!enabled)
                _playersWithInstructorEnabled.Remove(player.SteamID);
            return;
        }

        if (enabled)
        {
            if (_playersWithInstructorEnabled.Count == 0)
                Server.ExecuteCommand("sv_gameinstructor_disable false");

            player.ReplicateConVar("sv_gameinstructor_enable", "true");
            _playersWithInstructorEnabled.Add(player.SteamID);
            return;
        }

        _playersWithInstructorEnabled.Remove(player.SteamID);
        player.ReplicateConVar("sv_gameinstructor_enable", "false");

        if (_playersWithInstructorEnabled.Count == 0)
            Server.ExecuteCommand("sv_gameinstructor_disable true");
    }

    private static void SendConsoleMessage(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
