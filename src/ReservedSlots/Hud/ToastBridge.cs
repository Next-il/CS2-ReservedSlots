using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;

namespace ReservedSlots;

/// <summary>
/// Sends toasts through ToastService when it is installed, and reports failure so the caller can fall
/// back to the chat line it would have printed anyway. Same arrangement as AFKManager's ToastBridge.
///
/// <para><b>Why the Core split.</b> PanoramaManager.Toasts.Api.dll is an optional dependency and is
/// simply absent on a server without ToastService. A missing assembly raises FileNotFoundException
/// while JIT-compiling the first method whose body or signature names a type from it - before that
/// method's own try block runs - so a try/catch around the API call catches nothing and the exception
/// surfaces in the caller, which here is the kick timer itself. Every public member below names no type
/// from the toast API; every such reference lives in a NoInlining Core method.</para>
///
/// <para>Only <see cref="_available"/> - a bool - is cached. Holding the IToastApi in a field would put
/// the missing type into this class's static layout and defeat the whole arrangement, so the capability
/// is fetched again inside <see cref="ShowCore"/> on every toast; that is a dictionary lookup.</para>
/// </summary>
internal static class ToastBridge
{
    // const string, so the compiler embeds them as literals - naming a colour, an icon or a placement
    // here creates no runtime dependency on the toast API. These are the names ToastService's layout
    // defines (ToastColor / ToastIcon / ToastPlacement / ToastAnimation).
    internal const string ColorWarn = "warn";
    internal const string IconAlert = "alert";
    private const string PlacementTopCenter = "tc";
    private const string AnimFromTop = "from-top";

    /// <summary>How long a toast stays: the preview's 4.2s.</summary>
    private const float Seconds = 4.2f;

    // False until OnAllPluginsLoaded proves otherwise, and set back to false the moment a call throws -
    // a server without the dll should pay the load failure once, not once per toast.
    private static bool _available;

    /// <summary>True when ToastService answered the last probe.</summary>
    internal static bool Available => _available;

    /// <summary>
    /// Probes for the capability. Call from OnAllPluginsLoaded, never from Load: plugin load order is
    /// undefined, so in Load ToastService may not have registered yet.
    /// </summary>
    internal static void Resolve()
    {
        try
        {
            _available = ProbeCore();
        }
        catch (FileNotFoundException) { _available = false; /* ToastService not installed */ }
        catch (TypeLoadException) { _available = false; /* incompatible API version, or a duplicate dll */ }
        catch (Exception) { _available = false; }
    }

    /// <summary>
    /// Shows a toast at the top center, like the preview: neutral when <paramref name="color"/> is null.
    /// Returns false when it could not be shown, so the caller prints its chat line instead.
    /// </summary>
    internal static bool TryNotify(CCSPlayerController? player, string title, string? description,
        string? color = null, string? icon = null)
    {
        if (!_available) return false;
        if (player is not { IsValid: true } || player.IsBot) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;

        try
        {
            return ShowCore(player, title, description, color, icon);
        }
        catch (FileNotFoundException) { _available = false; return false; }
        catch (TypeLoadException) { _available = false; return false; }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------------------------------
    // Names the toast API types. NoInlining is load-bearing - see the class remarks.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ProbeCore() => global::PanoramaManager.Toasts.ToastApi.Capability.Get() != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ShowCore(CCSPlayerController player, string title, string? description,
        string? color, string? icon)
    {
        // Re-fetched rather than cached: ToastService can be unloaded and reloaded at runtime, and a stale
        // reference would then point at a dead plugin's implementation.
        var api = global::PanoramaManager.Toasts.ToastApi.Capability.Get();
        if (api == null) return false;

        api.Show(new global::PanoramaManager.Toasts.ToastOptions
        {
            Player = player,
            Title = title,
            Description = description,
            Color = color,
            Icon = icon,
            Seconds = Seconds,
            Placement = PlacementTopCenter,
            Enter = AnimFromTop,
            Exit = AnimFromTop,
            // The preview's toast has no bar
            Progress = false,
        });

        return true;
    }
}
