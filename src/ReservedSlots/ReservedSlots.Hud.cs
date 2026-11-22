using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;

namespace ReservedSlots;

/// <summary>"Hud" in ReservedSlots.json. Both keys are optional: a config without them gets these defaults.</summary>
public class ReservedSlotsHudConfig
{
    /// <summary>
    /// The kick card and the toasts. False brings back the instructor hint and the chat lines unchanged.
    /// </summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// "X was kicked" as a toast, to whoever "Display Kicked Players Message" sends the chat line to.
    /// False keeps that chat line.
    /// </summary>
    [JsonPropertyName("OthersToast")]
    public bool OthersToast { get; set; } = true;
}

/// <summary>
/// The HUD side of the plugin. Who is kicked, why and when is decided exactly as before; this only changes
/// what people see while it happens:
///
/// <list type="bullet">
/// <item>The kicked player gets the red kick card (Hud/KickHud.cs) for the kick delay instead of the
/// env_instructor_hint caption, counting down to the kick. With a delay of 0 there is no card, as there was
/// no hint. The disconnect still carries "Kick Reason", so the client's own reason text shows after.</item>
/// <item>The "Player X was kicked" chat line becomes one toast through ToastService, for the same
/// recipients as before (everyone, or admins). Anyone a toast can't reach gets the chat line.</item>
/// </list>
/// </summary>
public partial class ReservedSlots
{
    private KickHud? _hud;

    private bool HudOn => Config.Hud.Enabled && _hud is { Enabled: true };

    private void HudLoad()
    {
        if (!Config.Hud.Enabled)
            return;

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            // ToastService can be (re)loaded at any time; one probe per map keeps the answer fresh
            ToastBridge.Resolve();
            // Every pending kick was dropped with the old map (ResetTransientPlayerState)
            _hud?.Clear();
        });

        _hud = new KickHud(this);
        _hud.Init();

        if (_hud.Enabled)
            AddTimer(0.1f, HudTick, TimerFlags.REPEAT);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        // Not in Load: plugin load order is undefined, so ToastService may not have registered yet
        ToastBridge.Resolve();
    }

    public override void Unload(bool hotReload)
    {
        _hud?.Dispose();
        _hud = null;
    }

    private void HudTick() => _hud?.Tick(IsPendingKick);

    private bool IsPendingKick(ulong steamId, long token) =>
        waitingForKick.TryGetValue(steamId, out var pendingKick) && pendingKick.Token == token;

    private void HudDisconnect(CCSPlayerController player) => _hud?.OnDisconnect(player.Slot);

    /// <summary>
    /// The warning before a delayed kick: the card when it can be shown, otherwise the instructor hint as
    /// before. <paramref name="joiner"/> is who the room is made for, for the card's title.
    /// </summary>
    private void ShowKickWarning(CCSPlayerController player, KickReason reason, int delay, PendingKickState pendingKick, ReservedType joiner)
    {
        if (HudOn)
        {
            var (eyebrow, title, sub) = reason == KickReason.ServerIsFull
                ? (HudText("Card.ServerIsFull.Eyebrow", "Server full"),
                   HudText("Card.ServerIsFull.Title", "The server is full"),
                   HudText("Card.ServerIsFull.Sub", "The free slots are reserved. Try again later."))
                : (HudText("Card.ReservedPlayerJoined.Eyebrow", "Reserved slot"),
                   joiner == ReservedType.Admin
                       ? HudText("Card.ReservedPlayerJoined.TitleAdmin", "An admin joined a full server")
                       : HudText("Card.ReservedPlayerJoined.Title", "A VIP joined a full server"),
                   HudText("Card.ReservedPlayerJoined.Sub", "You are the one leaving. Thanks for playing."));

            if (_hud!.Show(player, pendingKick.Token, delay, eyebrow, title, sub))
                return;
        }

        ShowKickHint(player, reason, delay);
    }

    /// <summary>
    /// "Player X was kicked" to <paramref name="recipients"/> as a toast. False when toasts are off or
    /// ToastService is not there, so the caller prints the chat line as before. The kicked player is
    /// skipped: they are already on their way out.
    /// </summary>
    private bool HudKickToast(string name, string steamId, KickReason reason, string chatLine, IEnumerable<CCSPlayerController> recipients)
    {
        if (!Config.Hud.Enabled || !Config.Hud.OthersToast || !ToastBridge.Available)
            return false;

        var title = HudText("Toast.PlayerWasKicked.Title", "{0} was kicked", name);
        var description = reason == KickReason.ServerIsFull
            ? HudText("Toast.PlayerWasKicked.ServerIsFull", "The server is full")
            : HudText("Toast.PlayerWasKicked.ReservedPlayerJoined", "A reserved slot was needed");

        foreach (var player in recipients)
        {
            if (!IsHumanPlayer(player) || player.SteamID.ToString() == steamId)
                continue;

            if (!ToastBridge.TryNotify(player, title, description))
                player.PrintToChat(chatLine);
        }

        return true;
    }

    /// <summary>A phrase from lang/, or the English fallback when the server's lang files don't have it yet.</summary>
    private string HudText(string key, string fallback, params object[] args)
    {
        var text = Localizer[key, args];
        return text.ResourceNotFound ? string.Format(fallback, args) : text.Value;
    }
}
