using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MasterBaiter;

/// <summary>
/// Traeger der Dalamud-Dienste.
///
/// Muss eine eigene Klasse sein: <c>IDalamudPluginInterface.Create&lt;T&gt;()</c>
/// erzeugt eine neue Instanz von T und injiziert dabei die Dienste. Mit
/// <c>Create&lt;Plugin&gt;()</c> im Plugin-Konstruktor ruft sich dieser endlos
/// selbst auf.
/// </summary>
internal sealed class Services
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
}
