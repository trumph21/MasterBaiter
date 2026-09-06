using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MasterBaiter;

public sealed class Plugin : IDalamudPlugin
{
    // Bequeme Kuerzel auf die injizierten Dienste, siehe Services.cs
    internal static IDalamudPluginInterface PluginInterface => Services.PluginInterface;
    internal static IDataManager DataManager => Services.DataManager;
    internal static IGameGui GameGui => Services.GameGui;
    internal static IPluginLog Log => Services.Log;
    internal static IChatGui ChatGui => Services.ChatGui;
    internal static IFramework Framework => Services.Framework;
    internal static IClientState ClientState => Services.ClientState;
    internal static IObjectTable ObjectTable => Services.ObjectTable;
    internal static Dalamud.Plugin.Services.ITargetManager TargetManager => Services.TargetManager;
    internal static Dalamud.Plugin.Services.ICondition Condition => Services.Condition;

    private const string Command = "/masterbaiter";
    private const string CommandShort = "/mbait";

    private readonly WindowSystem _windows = new("MasterBaiter");
    private readonly MainWindow _main;
    private readonly Configuration _config;
    private readonly PurchaseQueue _queue;
    private readonly ScripSweep _sweep;
    private readonly MarketBoard _market;
    private readonly CosmicTravel _cosmic;
    private readonly Restock _restock;
    private VendorIndex _vendors = null!;

    /// <summary>
    /// Der Kauf nach der Ankunft wartet, bis der Laden seine Posten wirklich
    /// zeigt. Das Fenster existiert bereits, wenn es aufgeht, ist aber noch
    /// leer — wer im selben Moment nachsieht, findet nichts und meldet
    /// faelschlich "nichts zu kaufen".
    /// </summary>
    private const int ShopFillTimeoutMs = 6000;

    private bool _buyPending;
    private long _buyPendingUntil;

    /// <summary>Wie lange auf die anderen Plugins gewartet wird, bevor gemeckert wird.</summary>
    private const int HelperCheckDelayMs = 20000;

    private long _helperCheckAt;
    private readonly Travel _travel;
    private readonly Route _route;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();

        // Schneidet mit, wie das Planetenfenster auf einen echten Klick reagiert.
        CosmicPlanet.StartListening();

        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Pacing.Percent = _config.PacingPercent;

        // Frueh einstufen, damit die Zielmengen ab der ersten Tabelle stimmen.
        Tackle.Build();

        var baits = new BaitTable();
        var vendors = new VendorIndex();
        vendors.BuildAsync(baits.AllBaitIds);
        var restock = new Restock(_config, baits, new GatherList());
        _restock = restock;
        _queue = new PurchaseQueue();
        _sweep = new ScripSweep(restock, _queue, vendors);
        _market = new MarketBoard(_config, restock);
        _travel = new Travel();
        _cosmic = new CosmicTravel(_config, _travel, vendors);
        _route = new Route(_config, restock, vendors, _travel, _queue, _sweep, _market, _cosmic);

        // Waehrend einer Route steuert die Route den Kauf, nicht dieser Haken.
        _travel.Arrived += () =>
        {
            if (!_config.BuyOnArrival || _route.Running)
                return;

            // Waehrend einer Cosmic-Fahrt ist das angesprochene Ziel der
            // Fahrzeug-NPC, kein Haendler. Ohne diese Ausnahme laeuft die Frist
            // mitten in der Fahrt ab und meldet einen Fehlschlag, den es nicht
            // gibt. Nach der Ankunft startet die Fahrt selbst die Reise zum
            // Haendler, und dann greift der Vormerker richtig.
            if (_cosmic.Running)
                return;

            // Nicht sofort kaufen, sondern vormerken: siehe ShopFillTimeoutMs.
            _buyPending = true;
            _buyPendingUntil = Environment.TickCount64 + ShopFillTimeoutMs;
        };

        _vendors = vendors;
        // Auf dem Planeten angekommen: zum naechsten Haendler laufen, der etwas
        // Fehlendes fuehrt. Ein Teleport ist dort weder noetig noch moeglich,
        // die Reise beschraenkt sich also aufs Laufen.
        _cosmic.Arrival += () =>
        {
            restock.Refresh();
            var here = ClientState.TerritoryType;

            foreach (var row in restock.Rows)
            {
                if (row.Ignored || row.Missing <= 0)
                    continue;

                foreach (var vendor in vendors.For(row.BaitId))
                {
                    // Nach Standort fragen, nicht nach dem Teleportpunkt: Auf
                    // den Planeten gibt es keinen, und gebraucht wird er hier
                    // auch nicht — wir stehen bereits im Gebiet.
                    if (vendor.Territory != here || !vendor.HasPosition)
                        continue;

                    Log.Information($"[MasterBaiter] Walking to {vendor.Npc} for {row.Name}.");
                    _travel.Start(vendor);
                    return;
                }
            }

            Log.Information("[MasterBaiter] Nothing missing is sold here.");
        };

        _main = new MainWindow(_config, restock, _queue, _sweep, _market, _cosmic, vendors, _travel, _route);
        _windows.AddWindow(_main);

        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        Services.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shows missing fishing bait from your GatherBuddy list and restocks it at a vendor.",
        });
        Services.CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand) { ShowInHelp = false });

        Log.Information($"[MasterBaiter] loaded, {baits.FishCount} fish in the bait table.");

        // Die Pruefung auf vnavmesh und Lifestream kommt spaeter, siehe
        // CheckHelpers: Im Konstruktor sind die anderen Plugins je nach
        // Ladereihenfolge noch gar nicht da, und die Warnung waere ein
        // Fehlalarm im Log.
        _helperCheckAt = Environment.TickCount64 + HelperCheckDelayMs;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Die Teleportliste wird hier gelesen, nicht in der Zeichenroutine:
        // Sie aufzubauen ist ein Eingriff ins Spiel, kein Nachschlagen.
        StartPendingPurchase();
        CheckHelpers();
        _cosmic.Tick();
        Sprint.Tick(_config, _travel, _route, _cosmic);
        Teleportable.Tick();
        MarketBoards.Learn(_config);
        _queue.Tick();
        _sweep.Tick();
        _market.Tick();
        _travel.Tick();
        _route.Tick();
    }

    /// <summary>
    /// Startet den vorgemerkten Kauf, sobald der Laden seine Posten zeigt.
    /// </summary>
    private void StartPendingPurchase()
    {
        if (!_buyPending)
            return;

        if (MarketBoard.IsOpen)
        {
            _buyPending = false;
            _restock.Refresh();
            _market.Start(_restock.Rows, _vendors);
            return;
        }

        var now = Environment.TickCount64;

        // Das Fenster muss nicht nur offen sein, sondern auch Posten fuehren —
        // ausser beim Scrip-Tausch, der auf einem leeren Reiter oeffnen kann und
        // sich selbst durchblaettert.
        if (ShopWindowReader.IsOpen
            && (ShopWindowReader.Kind == "scrip exchange" || ShopWindowReader.ReadEntries().Count > 0))
        {
            _buyPending = false;
            _restock.Refresh();
            if (ShopWindowReader.Kind == "scrip exchange")
                _sweep.Start();
            else
                _queue.Start(_restock.Rows);
            return;
        }

        if (now <= _buyPendingUntil)
            return;

        _buyPending = false;
        Log.Warning(ShopWindowReader.Kind == "none"
            ? "[MasterBaiter] No shop window opened, so nothing was bought."
            : $"[MasterBaiter] The {ShopWindowReader.Kind} never listed anything; nothing was bought.");
    }

    /// <summary>
    /// Meldet einmalig, wenn vnavmesh oder Lifestream fehlen. Erst nach einer
    /// Wartezeit, damit die Ladereihenfolge keinen Fehlalarm ausloest.
    /// </summary>
    private void CheckHelpers()
    {
        if (_helperCheckAt == 0 || Environment.TickCount64 < _helperCheckAt)
            return;
        _helperCheckAt = 0;

        var missing = _travel.MissingHelpers();
        if (missing.Count > 0)
            Log.Warning($"[MasterBaiter] Required plugin(s) not found: {string.Join(" and ", missing)}. " +
                        "Travelling to vendors is disabled.");
    }

    private void OpenMain() => _main.IsOpen = true;

    private void OnCommand(string command, string args) => _main.Toggle();

    public void Dispose()
    {
        CosmicPlanet.StopListening();
        Framework.Update -= OnFrameworkUpdate;
        Services.CommandManager.RemoveHandler(Command);
        Services.CommandManager.RemoveHandler(CommandShort);
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windows.RemoveAllWindows();
    }
}
