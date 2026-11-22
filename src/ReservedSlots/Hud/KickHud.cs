using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using PanoramaManager;

namespace ReservedSlots;

/// <summary>
/// The kick card: the private red center panel the player about to be kicked sees for the kick delay,
/// in place of the env_instructor_hint caption. The layout is
/// hud/panorama/layout/custom_game/reservedslots_hud.xml, shipped in the workshop addon. It is
/// AFKManager's AFK card (AfkHud.cs) in red, driven the same way.
///
/// <para><b>One layout, opened for a player the first time they need it and left open.</b> Its root is an
/// invisible full-screen panel; the one card inside carries its own "show". The card is "hidden"
/// (collapsed) while not in use and un-collapsed a moment before it shows, so it always has a resting
/// place to slide in from.</para>
///
/// <para><b>Everything is drawn from state.</b> PanoramaManager rebuilds a player's panel by itself after a
/// respawn, a round restart or a map change, and drops every per-viewer variable and class on the way
/// (PanelAction.Restored). Each viewer's card is a plain record here and <see cref="Restore"/> writes it
/// back. Every timed step is a time in that record, run by <see cref="Tick"/>.</para>
///
/// <para>This class only draws. Who is kicked, why and when is the plugin's call, unchanged; each card
/// belongs to one pending kick (its token), and <see cref="Tick"/> takes it away when that kick is no
/// longer pending - cancelled because the player turned out to hold a reservation, or became immune.</para>
/// </summary>
internal sealed class KickHud : IDisposable
{
    private const string Layout = "panorama/layout/custom_game/reservedslots_hud.vxml_c";

    private const string CardId = "rs_card";
    private const string NumberId = "rs_n";
    private const string BarId = "rs_bar";

    /// <summary>Un-collapse first, then "show": a transition needs the client to have seen the card at
    /// its resting offset (ToastService found one server frame is not enough).</summary>
    private const float EntrySeconds = 0.06f;

    /// <summary>The exit transition, before the card collapses.</summary>
    private const float ExitSeconds = 0.35f;

    /// <summary>"run" off, wait, "run" on. A restart of the same class was measured to need 0.3s or more.</summary>
    private const float BarStartSeconds = 0.35f;

    private const int LastSeconds = 3;
    private const int BarSteps = 20;

    /// <summary>The dur-N classes reservedslots_hud.css defines: every second to 60, then every 5 to 180.</summary>
    private static readonly int[] BarDurations =
        Enumerable.Range(1, 60).Concat(Enumerable.Range(13, 24).Select(i => i * 5)).ToArray();

    private static readonly int MaxBarSeconds = BarDurations[^1];

    private sealed class Viewer
    {
        /// <summary>The pending kick this card belongs to.</summary>
        public ulong SteamId;
        public long Token;

        /// <summary>A card should be on screen.</summary>
        public bool Active;

        /// <summary>"show" is on.</summary>
        public bool Shown;

        /// <summary>When to write the card and turn "show" on; -1 when nothing is pending.</summary>
        public float ShowAt = -1;

        /// <summary>When to collapse a card that has finished leaving; -1 when nothing is pending.</summary>
        public float CollapseAt = -1;

        public string Eyebrow = "";
        public string Title = "";
        public string Sub = "";

        // countdown
        public float Deadline;
        public float Total = 1;
        public int Number = -1;
        public bool PopA;

        // bar: the classes on rs_bar as last written, so the next write can take them off again
        public int BarAt = -1;
        public int BarDur = -1;
        public bool BarStarted;
        public float BarRunAt = -1;
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Viewer> _viewers = [];
    private PanelHandle? _panel;

    public KickHud(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    public bool Enabled => _panel != null;

    /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
    public void Init()
    {
        if (_panel != null)
            return;

        try
        {
            Panorama.Init(_plugin);
            _panel = Panorama.Spawn(Layout, new LayoutContract
            {
                // Unique server-wide: dialog variables are keyed by panel id
                RootPanelId = "ReservedSlotsRoot",
                RevealClass = "show",
                RowCount = 0,
                CaptureInput = false,
                HideHud = HideHudFlags.None,
                SharedText = false,
            });
            _panel.OnEvent += OnPanelEvent;
            _plugin.Logger.LogInformation("[Reserved Slots] Kick HUD ready");
        }
        catch (Exception ex)
        {
            // The plugin works without its HUD: the caller falls back to the instructor hint when Enabled is false
            _plugin.Logger.LogWarning("[Reserved Slots] Kick HUD failed to start, using the instructor hint instead: {Message}", ex.Message);
            _panel = null;
        }
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _panel = null;
        _viewers.Clear();
    }

    private void OnPanelEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Restored && e.Player.IsValid)
            Restore(e.Player);
    }

    // ------------------------------------------------------------------ viewers

    private static bool IsHuman(CCSPlayerController? player) =>
        player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

    private Viewer ViewerOf(CCSPlayerController player)
    {
        if (!_viewers.TryGetValue(player.Slot, out var viewer))
        {
            viewer = new Viewer();
            _viewers[player.Slot] = viewer;
        }

        return viewer;
    }

    /// <summary>Opens the player's session if it is not open yet, which also redraws what they should see.</summary>
    private bool EnsureOpen(CCSPlayerController player)
    {
        if (_panel == null || !IsHuman(player))
            return false;

        if (!_panel.IsOpenFor(player))
        {
            _panel.Open(player);
            Restore(player);
        }

        return _panel.IsOpenFor(player);
    }

    public void OnDisconnect(int slot)
    {
        _viewers.Remove(slot);
        _panel?.Close(slot);
    }

    /// <summary>Map start: the clock restarts on the new map and every pending kick was dropped with the old one.</summary>
    public void Clear()
    {
        foreach (var viewer in _viewers.Values)
        {
            viewer.Active = false;
            viewer.Shown = false;
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            viewer.BarStarted = false;
            viewer.BarRunAt = -1;
        }
    }

    /// <summary>
    /// Writes the card back for one player, from state. Called on open and after the library rebuilt the
    /// panel. Every class it may have lost is written again; the bar starts over from where it should be.
    /// </summary>
    public void Restore(CCSPlayerController player)
    {
        if (_panel == null || !_panel.IsOpenFor(player))
            return;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        viewer.Shown = false;
        viewer.Number = -1;
        viewer.BarStarted = false;
        viewer.BarRunAt = -1;
        // The rebuilt panel has none of the bar's classes
        viewer.BarAt = -1;
        viewer.BarDur = -1;
        Cls(player, CardId, "show", false);

        if (!viewer.Active)
        {
            viewer.ShowAt = -1;
            viewer.CollapseAt = -1;
            Cls(player, CardId, "hidden", true);
            return;
        }

        Cls(player, CardId, "hidden", false);
        viewer.CollapseAt = -1;
        viewer.ShowAt = now + EntrySeconds;
    }

    // ------------------------------------------------------------------ the card

    /// <summary>
    /// Shows the kick card for one pending kick: <paramref name="seconds"/> is the kick delay, so the number
    /// reaches 0 when the plugin's timer kicks. Returns false when the card cannot be shown to this player,
    /// so the caller shows the instructor hint instead.
    /// </summary>
    public bool Show(CCSPlayerController player, long token, float seconds, string eyebrow, string title, string sub)
    {
        if (!EnsureOpen(player))
            return false;

        var viewer = ViewerOf(player);
        var now = Server.CurrentTime;

        viewer.SteamId = player.SteamID;
        viewer.Token = token;
        viewer.Active = true;
        viewer.Eyebrow = eyebrow;
        viewer.Title = title;
        viewer.Sub = sub;
        viewer.Deadline = now + seconds;
        viewer.Total = Math.Max(seconds, 0.1f);
        viewer.Number = -1;
        viewer.BarStarted = false;
        viewer.BarRunAt = -1;

        Cls(player, CardId, "hidden", false);
        viewer.CollapseAt = -1;

        if (viewer.Shown)
            Draw(player, viewer, now);
        else
            viewer.ShowAt = now + EntrySeconds;

        return true;
    }

    private void Hide(CCSPlayerController player, Viewer viewer, float now)
    {
        viewer.Active = false;
        viewer.ShowAt = -1;
        viewer.BarRunAt = -1;

        if (_panel == null || !_panel.IsOpenFor(player))
        {
            viewer.Shown = false;
            return;
        }

        // The content stays as it is, so the card fades out showing what it showed
        Cls(player, CardId, "show", false);
        viewer.Shown = false;
        viewer.CollapseAt = now + ExitSeconds;
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>
    /// Runs every timed step. The plugin calls this ten times a second while the HUD is on.
    /// <paramref name="isPending"/> answers whether a (SteamID, token) kick is still waiting to go out.
    /// </summary>
    public void Tick(Func<ulong, long, bool> isPending)
    {
        if (_panel == null || _viewers.Count == 0)
            return;

        var now = Server.CurrentTime;

        foreach (var (slot, viewer) in _viewers)
        {
            if (!viewer.Active && viewer.CollapseAt < 0)
                continue;

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true } || !_panel.IsOpenFor(player))
                continue;

            // The kick was called off (or the slot belongs to someone else now): take the card away
            if (viewer.Active && (player.SteamID != viewer.SteamId || !isPending(viewer.SteamId, viewer.Token)))
                Hide(player, viewer, now);

            if (viewer.CollapseAt >= 0 && now >= viewer.CollapseAt)
            {
                viewer.CollapseAt = -1;
                if (!viewer.Active)
                {
                    Cls(player, CardId, "hidden", true);
                    // Out of sight now, so the bar can go back to full for the next card
                    BarSnapTo(player, viewer, 1f);
                }
            }

            if (viewer.ShowAt >= 0 && now >= viewer.ShowAt && viewer.Active)
            {
                viewer.ShowAt = -1;
                Draw(player, viewer, now);
                Cls(player, CardId, "show", true);
                viewer.Shown = true;
            }

            if (viewer.Active && viewer.Shown)
                TickCountdown(player, viewer, now);
        }
    }

    private void TickCountdown(CCSPlayerController player, Viewer viewer, float now)
    {
        var remaining = viewer.Deadline - now;
        var number = Math.Max(0, (int)Math.Ceiling(remaining - 0.05f));

        if (number != viewer.Number)
        {
            viewer.Number = number;
            Set(player, "rs_n", number.ToString());

            if (number <= LastSeconds)
            {
                // Ping-pong: the only restart a class diff can carry
                viewer.PopA = !viewer.PopA;
                Cls(player, NumberId, "pop-a", viewer.PopA);
                Cls(player, NumberId, "pop-b", !viewer.PopA);
            }
        }

        // A delay longer than the longest dur-N class keeps a full bar until it fits
        if (!viewer.BarStarted && remaining <= MaxBarSeconds + 0.5f)
        {
            viewer.BarStarted = true;
            // Snap it to where it should be, then drain after the wait
            BarSnapTo(player, viewer, remaining / viewer.Total);
            viewer.BarRunAt = now + BarStartSeconds;
        }

        if (viewer.BarRunAt >= 0 && now >= viewer.BarRunAt)
        {
            viewer.BarRunAt = -1;
            BarRun(player, viewer, viewer.Deadline - now);
        }
    }

    // ------------------------------------------------------------------ the bar

    /// <summary>
    /// Puts the bar at a fraction with no transition. The duration comes off before "run" so the reset
    /// snaps; with a dur-N class still on, dropping "run" would animate the bar back to full over it.
    /// </summary>
    private void BarSnapTo(CCSPlayerController player, Viewer viewer, float fraction)
    {
        if (viewer.BarDur >= 0)
            Cls(player, BarId, $"dur-{viewer.BarDur}", false);
        viewer.BarDur = -1;
        Cls(player, BarId, "run", false);

        var at = Math.Clamp((int)Math.Round(fraction * BarSteps), 0, BarSteps);
        if (viewer.BarAt >= 0 && viewer.BarAt != at)
            Cls(player, BarId, $"at-{viewer.BarAt}", false);
        Cls(player, BarId, $"at-{at}", true);
        viewer.BarAt = at;
    }

    /// <summary>Starts the drain: one linear clip transition to 0 over the time that is left.</summary>
    private void BarRun(CCSPlayerController player, Viewer viewer, float seconds)
    {
        if (seconds <= 0)
            return;

        var dur = BarDurations.OrderBy(d => Math.Abs(d - seconds)).First();
        Cls(player, BarId, $"dur-{dur}", true);
        viewer.BarDur = dur;
        Cls(player, BarId, "run", true);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Writes the card's text, then the number and the bar from the clock.</summary>
    private void Draw(CCSPlayerController player, Viewer viewer, float now)
    {
        Set(player, "rs_eyebrow", viewer.Eyebrow);
        Set(player, "rs_title", viewer.Title);
        Set(player, "rs_sub", viewer.Sub);
        viewer.Number = -1;
        TickCountdown(player, viewer, now);
    }

    private void Set(CCSPlayerController player, string name, string value) => _panel!.SetVariableFor(player, name, value);

    private void Cls(CCSPlayerController player, string id, string cls, bool on) => _panel!.SetClassFor(player, id, cls, on);
}
