using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MasterBaiter;

internal sealed class MainWindow : Window
{
    private readonly Configuration _config;
    private readonly Restock _restock;
    private readonly PurchaseQueue _queue;
    private readonly ScripSweep _sweep;
    private readonly MarketBoard _market;
    private readonly CosmicTravel _cosmic;

    // Die Routenplanung laeuft ueber alle Koeder und alle ihre Haendler. Das
    // gehoert nicht in jedes Bild, nur weil der Knopf eine Zahl anzeigt.
    private List<Route.RouteStop> _plan = [];
    private long _planAt;

    // Dasselbe fuer das naechste Marktbrett: Die Suche geht ueber alle Bretter
    // und fuer jedes ueber alle Aetheryten. Einmal je Sekunde reicht.
    private VendorIndex.Vendor? _board;
    private long _boardAt;
    private readonly VendorIndex _vendors;
    private readonly RetainerStock _retainers;
    private readonly Travel _travel;
    private readonly Route _route;
    // Welche Zeile gerade ihr Ziel bearbeitet. 0 heisst: keine.
    private uint _editing;
    private bool _grabFocus;

    // Suchtext ueber der Tabelle. Mit "Show all fishing tackle" sind es 188
    // Zeilen; darin etwas zu finden, ohne zu suchen, ist Blaettern.
    private string _filter = string.Empty;

    // Die Vorschau wird im selben Frame geoeffnet, in dem der Knopf gedrueckt
    // wurde; ImGui verlangt OpenPopup und BeginPopup im gleichen ID-Bereich.
    private bool _openPreview;

    private string _message = string.Empty;
    private DateTime _messageUntil;
    private bool _wasRunning;

    public MainWindow(Configuration config, Restock restock, PurchaseQueue queue, ScripSweep sweep, MarketBoard market, CosmicTravel cosmic, VendorIndex vendors, Travel travel, Route route, RetainerStock retainers)
        : base($"MasterBaiter {VersionText}###MasterBaiterMain")
    {
        _config = config;
        _restock = restock;
        _queue = queue;
        _sweep = sweep;
        _market = market;
        _cosmic = cosmic;
        _vendors = vendors;
        _retainers = retainers;
        _travel = travel;
        _route = route;
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// Die Fassung fuer die Titelleiste. Aus der Assembly gelesen, nicht
    /// danebengeschrieben: Eine Zahl, die von Hand gepflegt wird, steht
    /// frueher oder spaeter falsch da. Die vierte Stelle ist immer null und
    /// faellt weg.
    /// </summary>
    private static string VersionText
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v == null ? string.Empty : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public override void OnOpen() => _restock.Refresh();

    public override void Draw()
    {
        // Bestaende jeden Frame nachziehen, damit die Tabelle waehrend eines
        // Kaufdurchlaufs mitlaeuft. Die Listendatei wird dabei nicht angefasst.
        _restock.RefreshCounts();
        _restock.AnnotateShop();

        // Nach dem Ende eines Durchlaufs einmal vollstaendig neu einlesen.
        var busy = _queue.Running || _sweep.Running || _market.Running;
        if (_wasRunning && !busy)
            _restock.Refresh();
        _wasRunning = busy;

        if (!ImGui.BeginTabBar("##masterbaiter"))
            return;

        if (ImGui.BeginTabItem("Baits"))
        {
            DrawBaitsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Options"))
        {
            DrawOptionsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug"))
        {
            DrawDebugTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---------- Reiter "Baits" ----------

    private void DrawBaitsTab()
    {
        DrawActionBar();
        DrawWarnings();
        ImGui.Separator();

        if (_restock.Status != null)
        {
            ImGui.TextWrapped($"Could not read the list: {_restock.Status}");
            return;
        }

        if (_restock.Rows.Count == 0)
        {
            ImGui.TextWrapped("No fish in the auto-gather list need bait.");
            return;
        }

        DrawFilter();
        DrawTable();
        DrawRoutePreview();
    }

    /// <summary>
    /// Nur was man im Spielgeschehen braucht. Alles, was man einmal einstellt
    /// und dann vergisst, steht im anderen Reiter.
    /// </summary>
    private void DrawActionBar()
    {
        if (ImGui.Button("Refresh"))
            _restock.Refresh();

        ImGui.SameLine();

        var shopOpen = ShopWindowReader.IsOpen;

        // Kein "Buy missing" mehr. Gekauft wird, wo das Plugin selbst
        // hingefahren ist — ueber "Run route" oder "Go" mit "buy on arrival".
        // Fuer den Einzelfall am offenen Laden bleibt der Buy-Knopf in der
        // jeweiligen Zeile.
        if (_queue.Running || _sweep.Running)
        {
            if (ImGui.Button("Cancel"))
            {
                _sweep.Stop("Stopped.");
                _queue.Stop("Stopped.");
            }

            ImGui.SameLine();
        }

        // Kein eigener Marktbrett-Knopf mehr: Die Route nimmt das Brett als
        // letzten Halt mit, und "buy on arrival" loest den Kauf dort aus. Ein
        // Knopf, der nur greift, waehrend man selbst davorsteht, war die
        // Ausnahme und nicht die Regel.
        if (_market.Running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop market board"))
                _market.Stop("Stopped.");
        }

        ImGui.SameLine();
        if (_route.Running)
        {
            if (ImGui.Button("Stop route"))
                _route.Stop("Route stopped.");
        }
        else
        {
            var plan = CachedPlan();
            using (ImRaiiDisabled(plan.Count == 0 || _travel.Running || _queue.Running
                                  || _sweep.Running || _market.Running || !_travel.Available))
            {
                if (ImGui.Button(plan.Count > 0 ? $"Run route ({plan.Count} stops)" : "Run route"))
                    _route.Start();
            }
            if (plan.Count > 0 && ImGui.IsItemHovered())
            {
                // Die Ladenart gehoert dazu, sonst widerspricht diese Liste der
                // Haendlerliste in der Tabelle, ohne dass man den Grund sieht:
                // Dort steht die guenstigere Waehrung oben, hier gewinnt, wer
                // die meisten Koeder in einem Halt abdeckt.
                var lines = plan.Select((s, i) =>
                    $"{i + 1}. {s.Vendor.Npc} - {s.Vendor.Zone} [{s.Vendor.KindName}]: " +
                    string.Join(", ", s.Baits.Take(6)) +
                    (s.Baits.Count > 6 ? $" (+{s.Baits.Count - 6})" : string.Empty)).ToList();

                lines.Add(string.Empty);
                lines.Add("Fewest stops wins, so a vendor covering more baits beats a cheaper currency.");

                ImGui.SetTooltip(string.Join(Environment.NewLine, lines));
            }

            ImGui.SameLine();
            using (ImRaiiDisabled(plan.Count == 0))
            {
                if (ImGui.Button("Preview"))
                    _openPreview = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Every stop with what it buys and what it costs, before anything moves.");
        }

        if (_travel.Running && !_route.Running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop travel"))
                _travel.Stop("Travel stopped.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(shopOpen ? "vendor open" : "no vendor open");

        var status = CurrentStatus();
        if (status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(status);
        }
        else if (_message.Length > 0 && DateTime.Now < _messageUntil)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_message);
        }
    }

    /// <summary>Was gerade laeuft, in der Reihenfolge der Dringlichkeit.</summary>
    private string CurrentStatus()
    {
        if (_sweep.Running && _sweep.Status.Length > 0)
            return _sweep.Status;
        if (_market.Running && _market.Status.Length > 0)
            return _market.Status;
        if (_queue.Status.Length > 0)
            return _queue.Status;
        if (_route.Running && _route.Status.Length > 0)
            return _route.Status;
        return _travel.Status;
    }

    private void DrawWarnings()
    {
        var missingHelpers = _travel.MissingHelpers();
        if (missingHelpers.Count > 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                $"Missing: {string.Join(" and ", missingHelpers)} - required for Go and Run route.");

        if (!_vendors.Ready || _restock.Rows.Count == 0)
            return;

        // Nur die Koeder, die deine Fische brauchen. Sind alle aufgelistet,
        // waeren sonst hundert Event- und Belohnungskoeder ohne Quelle dabei,
        // und die Zeile sähe nach einem Rueckschritt aus, obwohl nichts fehlt.
        var relevant = _restock.Rows.Where(r => r.Needed).ToList();
        var orphans = relevant.Where(r => _vendors.For(r.BaitId).Count == 0
                                          && _vendors.OtherSourcesFor(r.BaitId).Count == 0
                                          && _vendors.RecipeFor(r.BaitId) == null).ToList();

        ImGui.TextDisabled(orphans.Count == 0
            ? $"All {relevant.Count} baits your fish need can be bought or crafted."
            : $"{orphans.Count} of {relevant.Count} needed baits have no shop and no recipe.");
        if (orphans.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(Environment.NewLine, orphans.Take(20).Select(r => r.Name)));

        // Der Vorrat bei den Gehilfen laesst sich nicht erfragen, nur
        // mitschreiben. Wer das nicht weiss, haelt eine fehlende Zahl fuer
        // eine Null — also einmal sagen, was zu tun ist, statt es im
        // Hinweistext zu verstecken.
        if (!_config.CountRetainers)
            return;

        var (total, seen) = _retainers.Coverage();

        if (total > 0 && seen >= total)
            return;
        if (total == 0 && seen > 0)
            return;

        ImGui.TextColored(Wanted, total > 0
            ? seen == 0
                ? $"None of your {total} retainers have been counted yet."
                : $"{total - seen} of {total} retainers have not been counted yet."
            : "No retainer has been counted yet.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The game only hands out a retainer's contents while that retainer is open." +
                Environment.NewLine +
                "Open each one once at a summoning bell and the bait in it counts towards" +
                Environment.NewLine +
                "your target, so it is not bought a second time.");
    }

    // ---------- Reiter "Options" ----------

    private void DrawOptionsTab()
    {
        Section("Stock levels");
        ImGui.TextDisabled("A lure costs a thousand times what a bait does, so they count separately.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(140);
        var target = _config.DefaultTarget;
        if (ImGui.InputInt("Bait target", ref target))
        {
            _config.DefaultTarget = Math.Clamp(target, 0, 9999);
            _config.Save();
            _restock.Refresh();
        }

        ImGui.SetNextItemWidth(140);
        var lureTarget = _config.DefaultLureTarget;
        if (ImGui.InputInt("Lure target", ref lureTarget))
        {
            _config.DefaultLureTarget = Math.Clamp(lureTarget, 0, 9999);
            _config.Save();
            _restock.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{Tackle.LureCount} fishing tackle items count as lures.");

        var countSaddle = _config.CountSaddlebag;
        if (ImGui.Checkbox("Count what is in your saddlebag", ref countSaddle))
        {
            _config.CountSaddlebag = countSaddle;
            _config.Save();
            _restock.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(Environment.NewLine,
                "Bait in your saddlebag counts towards the target, so it is not bought twice.",
                "Off: what is in your saddlebag is ignored.",
                "Counted only after you have opened it once — " +
                (_restock.SaddlebagSeen is { } seenAt
                    ? $"last look {Ago(seenAt)}."
                    : "not counted yet."),
                "The saddlebag opens anywhere."));

        var countRetainers = _config.CountRetainers;
        if (ImGui.Checkbox("Count what your retainers hold", ref countRetainers))
        {
            _config.CountRetainers = countRetainers;
            _config.Save();
            _restock.Refresh();
        }
        if (ImGui.IsItemHovered())
        {
            // Beide Schalter tun dasselbe an verschiedenen Orten, also sagen
            // sie es auch im selben Bau: was es bewirkt, was Aus bedeutet, was
            // schon gezaehlt ist, und wie man drankommt. Was sich unterscheidet,
            // faellt dann von selbst auf.
            var (total, seen) = _retainers.Coverage();
            ImGui.SetTooltip(string.Join(Environment.NewLine,
                "Bait with your retainers counts towards the target, so it is not bought twice.",
                "Off: what your retainers hold is ignored.",
                "Counted only after you have opened them once — " +
                (total > 0
                    ? $"{seen} of {total} done."
                    : seen > 0 ? $"{seen} counted so far." : "none counted yet."),
                "A retainer needs a summoning bell."));
        }

        Section("Market board");
        ImGui.TextDisabled("Prices here are set by players, not by the game, so both limits always apply.");
        ImGui.Spacing();

        var useMarket = _config.UseMarketBoard;
        if (ImGui.Checkbox("Buy from the market board", ref useMarket))
        {
            _config.UseMarketBoard = useMarket;
            _config.Save();
        }

        using (ImRaiiDisabled(!_config.UseMarketBoard))
        {
            ImGui.SetNextItemWidth(140);
            var maxUnit = _config.MarketMaxUnitPrice;
            if (ImGui.InputInt("Max gil per item", ref maxUnit))
            {
                _config.MarketMaxUnitPrice = Math.Clamp(maxUnit, 1, 99999999);
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Listings above this price per item are left alone.");

            ImGui.SetNextItemWidth(140);
            var maxRun = _config.MarketMaxGilPerRun;
            if (ImGui.InputInt("Max gil per run", ref maxRun))
            {
                _config.MarketMaxGilPerRun = Math.Clamp(maxRun, 1, 999999999);
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The run stops once it has spent this much.");
        }

        ImGui.TextDisabled("A stack larger than what you are missing is never bought, at any price.");

        Section("Bait list");
        var showAll = _config.ShowAllTackle;
        if (ImGui.Checkbox("Show all fishing tackle", ref showAll))
        {
            _config.ShowAllTackle = showAll;
            _config.Save();
            _restock.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists every bait and lure in the game, not just the ones your fish need." +
                             Environment.NewLine +
                             "The extra ones start at target 0 and are never bought until you set one.");

        var onlyEnabled = _config.OnlyEnabledLists;
        if (ImGui.Checkbox("Only lists enabled in GatherBuddy", ref onlyEnabled))
        {
            _config.OnlyEnabledLists = onlyEnabled;
            _config.Save();
            _restock.Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: every list is counted, including the ones you switched off there.");

        Section("Travel");
        ImGui.SetNextItemWidth(140);
        var pacing = _config.PacingPercent;
        if (ImGui.InputInt("Speed %", ref pacing, 10))
        {
            _config.PacingPercent = Math.Clamp(pacing, 10, 400);
            _config.Save();
            Pacing.Percent = _config.PacingPercent;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How long the plugin waits between actions, as a percentage." + Environment.NewLine +
                             "100 is the default, 50 is twice as brisk, 200 twice as leisurely." +
                             Environment.NewLine +
                             "Going much below 50 makes the game miss steps: windows need a moment to fill.");

        var buyOnArrival = _config.BuyOnArrival;
        if (ImGui.Checkbox("Buy on arrival", ref buyOnArrival))
        {
            _config.BuyOnArrival = buyOnArrival;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After travelling, start buying as soon as the shop opens.");

        var useSprint = _config.UseSprint;
        if (ImGui.Checkbox("Use Sprint", ref useSprint))
        {
            _config.UseSprint = useSprint;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses Sprint whenever it is off cooldown, but only while the plugin is " +
                             "travelling." + Environment.NewLine +
                             "Not while you are playing yourself, in combat or mounted.");

        var helpers = _travel.MissingHelpers();
        ImGui.TextDisabled(helpers.Count == 0
            ? "vnavmesh and Lifestream are both present."
            : $"Missing: {string.Join(" and ", helpers)}.");

        Section("Cosmic Exploration");
        ImGui.TextDisabled("No aetheryte goes there. The way is Drivingway, the Moon Rover in Mare Lamentorum.");
        ImGui.Spacing();

        var planets = CosmicPlanet.FromSheet();
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("Planet", _config.CosmicPlanet))
        {
            foreach (var planet in planets)
            {
                if (ImGui.Selectable(planet, planet == _config.CosmicPlanet))
                {
                    _config.CosmicPlanet = planet;
                    _config.Save();
                }
            }

            ImGui.EndCombo();
        }

        if (_cosmic.Running)
        {
            if (ImGui.Button("Stop"))
                _cosmic.Stop("Stopped.");
        }
        else
        {
            using (ImRaiiDisabled(!_cosmic.GateKnown || _travel.Running || !_travel.Available))
            {
                if (ImGui.Button($"Travel to {_config.CosmicPlanet}"))
                    _cosmic.Start();
            }
        }

        var autoPlanet = _config.AutoSelectPlanet;
        if (ImGui.Checkbox("Pick the planet automatically", ref autoPlanet))
        {
            _config.AutoSelectPlanet = autoPlanet;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: the window stays open and you click the planet and Blast Off yourself. " +
                             "The plugin does everything before and after.");

        if (_cosmic.GateIsLearned)
        {
            ImGui.SameLine();
            if (ImGui.Button("Forget NPC"))
                _cosmic.ForgetGate();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drop the remembered NPC and go back to the built-in one.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_cosmic.GateIsLearned
            ? $"via {_cosmic.GateName} (remembered)"
            : $"via {_cosmic.GateName}");

        if (_cosmic.Status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_cosmic.Status);
        }

        Section("Interface");
        var chat = _config.ChatFeedback;
        if (ImGui.Checkbox("Report in chat", ref chat))
        {
            _config.ChatFeedback = chat;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("One line in the game chat when a run or a route finishes." +
                             Environment.NewLine +
                             "Only you see it. Everything else stays in /xllog.");

        var honey = _config.HoneyTheme;
        if (ImGui.Checkbox("Y E L L O W theme", ref honey))
        {
            _config.HoneyTheme = honey;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tints this window honey yellow." + Environment.NewLine +
                             "Off: it follows your Dalamud style like every other window.");
    }

    /// <summary>
    /// Werkzeuge, um herauszufinden, warum etwas nicht geht. Ein eigener Reiter,
    /// weil sie keine Einstellungen sind: Wer die Zielmenge aendern will, soll
    /// nicht an vier Knoepfen vorbei, die ins Protokoll schreiben.
    /// </summary>
    private void DrawDebugTab()
    {
        ImGui.TextDisabled("All of these write to /xllog. Nothing here changes what the plugin does.");
        ImGui.Spacing();

        using (ImRaiiDisabled(!_vendors.Ready))
        {
            if (ImGui.Button("Self-check"))
            {
                _restock.Refresh();
                SelfCheck.Run(_restock, _vendors);
                Notify("Self-check written to log.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Check every bait for missing sources or unreachable zones.");

        ImGui.SameLine();
        using (ImRaiiDisabled(!ShopWindowReader.IsOpen))
        {
            if (ImGui.Button("Dump shop"))
            {
                ShopWindowReader.DumpToLog();
                Notify("Shop window written to log.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every entry of the open shop with its purchase index, price and currency.");

        ImGui.SameLine();
        if (ImGui.Button("Dump windows"))
        {
            AddonDump.Run();
            Notify("Open windows written to log.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("List every visible game window with its raw values. " +
                             "Useful when something is not recognised.");

        ImGui.SameLine();
        if (ImGui.Button("Export log to desktop"))
        {
            LogExport.Run(_config, _vendors, out var result);
            Notify(result);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes this plugin’s log lines to a text file on your desktop, " +
                             "with your settings at the top." + Environment.NewLine +
                             "Only lines from MasterBaiter — other plugins log character names " +
                             "and party data, and those stay out of the file.");

        if (_message.Length > 0 && DateTime.Now < _messageUntil)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_message);
        }

        ImGui.Spacing();
        ImGui.TextDisabled(_vendors.Ready
            ? $"{Tackle.LureCount} lures known, {_vendors.VendorCount} vendor entries, " +
              $"{Teleportable.Count} teleport destinations, " +
              $"{_retainers.Coverage().Seen} of {_retainers.Coverage().Total} retainers counted, " +
              (_restock.SaddlebagRead ? "saddlebag counted." : "saddlebag not counted yet.")
            : "Building the vendor index...");
    }

    /// <summary>Ueberschrift eines Abschnitts im Einstellungsreiter.</summary>
    private void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(_config.HoneyTheme ? Honey : new Vector4(0.6f, 0.8f, 1f, 1f), title);
        ImGui.Spacing();
    }

    /// <summary>
    /// Was die Route vorhat, bevor sie losfaehrt: jeder Halt, was dort gekauft
    /// wird und was es kostet.
    ///
    /// Der Hinweistext am Knopf konnte das nicht: Er verschwindet beim
    /// Wegsehen, fasst nach sechs Koedern zusammen und nennt keine Preise. Was
    /// ein Durchlauf kostet, sollte man vorher wissen und nicht hinterher.
    /// </summary>
    private void DrawRoutePreview()
    {
        if (_openPreview)
        {
            ImGui.OpenPopup("Route preview");
            _openPreview = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Route preview", ref open, ImGuiWindowFlags.NoSavedSettings))
            return;

        var plan = CachedPlan();
        var byName = _restock.Rows.ToDictionary(r => r.Name, r => r);
        var totals = new Dictionary<string, long>();
        var unknown = 0;
        var items = 0;

        ImGui.BeginChild("##stops", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2.4f));

        for (var i = 0; i < plan.Count; i++)
        {
            var stop = plan[i];

            ImGui.TextColored(_config.HoneyTheme ? Honey : new Vector4(0.6f, 0.8f, 1f, 1f),
                $"{i + 1}. {stop.Vendor.Npc}");
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"{stop.Vendor.Zone} [{stop.Vendor.KindName}]");

            if (ImGui.BeginTable($"##stop{i}", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn("bait", ImGuiTableColumnFlags.WidthStretch, 2.4f);
                ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("cost", ImGuiTableColumnFlags.WidthStretch, 1.6f);

                foreach (var name in stop.Baits)
                {
                    if (!byName.TryGetValue(name, out var row))
                        continue;

                    items += row.Missing;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(name);

                    ImGui.TableNextColumn();
                    Centered(row.Missing.ToString(), Wanted);

                    ImGui.TableNextColumn();

                    // Am Marktbrett steht der Preis erst fest, wenn gefragt
                    // wurde. Vorher zu schaetzen waere geraten.
                    PriceTag? found = null;
                    string? wrongCurrency = null;

                    if (stop.IsMarketBoard)
                    {
                        if (_market.QuoteFor(row.BaitId) is { UnitPrice: > 0 } quote
                            && PriceTag.TryParse($"{quote.UnitPrice} gil", out var boardTag))
                            found = boardTag;
                    }
                    else
                    {
                        // Alle bekannten Preise, aber nur der zaehlt, dessen
                        // Waehrung dieser Halt nimmt.
                        foreach (var candidate in _vendors.PricesFor(row.BaitId))
                        {
                            if (!PriceTag.TryParse(candidate, out var tag))
                                continue;

                            if (tag.FitsVendor(stop.Vendor.Kind))
                            {
                                found = tag;
                                break;
                            }

                            wrongCurrency ??= tag.Currency;
                        }
                    }

                    if (found is { } price)
                    {
                        var cost = price.TotalFor(row.Missing);
                        totals[price.Currency] = totals.GetValueOrDefault(price.Currency) + cost;
                        Priced($"{cost:N0} {price.Currency}", Dim);
                    }
                    else
                    {
                        unknown++;
                        Centered("?", Dim);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(stop.IsMarketBoard
                                ? "Market board prices are only known once the board has been asked."
                                : wrongCurrency != null
                                    ? $"Only a price in {wrongCurrency} is known, and this vendor does not take that."
                                    : "No price in the game data for this one.");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.TextUnformatted(plan.Count == 1
            ? $"1 stop, {items} items"
            : $"{plan.Count} stops, {items} items");

        if (totals.Count > 0)
        {
            var sums = totals.OrderByDescending(t => t.Value)
                .Select(t => $"{t.Value:N0} {t.Key}")
                .ToList();

            ImGui.SameLine();
            ImGui.TextColored(Dim, "-  " + string.Join(", ", sums.Select(Shorten)));

            // Dieselbe Auskunft wie in der Kostenspalte: Wer "1.926 Purple GS"
            // liest und nicht vorher eine Zeile darueber angefahren ist,
            // erfaehrt sonst nirgends, wofuer das Kuerzel steht.
            if (ImGui.IsItemHovered() && sums.Any(sum => Shorten(sum) != sum))
                ImGui.SetTooltip(string.Join(Environment.NewLine, sums));
        }

        if (unknown > 0)
        {
            ImGui.TextColored(Dim, unknown == 1
                ? "1 bait has no known price, so it is not in the total."
                : $"{unknown} baits have no known price, so they are not in the total.");
        }

        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running
                              || _market.Running || !_travel.Available))
        {
            if (ImGui.Button("Start route"))
            {
                _route.Start();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// <summary>
    /// Bringt Scrip-Waehrungen auf ihr Kuerzel: "Purple Gatherers' Scrip" wird
    /// zu "Purple GS", "Purple Crafters' Scrip" zu "Purple CS".
    ///
    /// Ausgeschrieben passt keine Scrip-Zeile in eine vertretbare Preisspalte,
    /// und abgeschnitten steht dort "1 Purple Gath" — was nicht falsch ist,
    /// aber auch nicht lesbar. Die Farbe bleibt deshalb stehen, sie haelt die
    /// Tauschwaehrungen auseinander; nur der lange Rest wird zum Kuerzel.
    ///
    /// Absichtlich ein Muster statt einer Liste: Die Farben wechseln mit jeder
    /// Erweiterung, die Bauform "Farbe Rolle Scrip" nicht. Das Apostroph
    /// schreibt das Spiel mal als ' und mal als U+2019, beides muss passen.
    /// </summary>
    private static readonly Regex ScripName =
        new(@"\b(Gatherers|Crafters)['’]?s? Scrip\b", RegexOptions.Compiled);

    private static string Shorten(string text) =>
        ScripName.Replace(text, m => $"{m.Groups[1].Value[0]}S");

    /// <summary>
    /// Alle bekannten Preise eines Koeders: der erste in der Spalte, saemtliche
    /// im Hinweistext.
    ///
    /// Mancher Koeder ist in zwei Waehrungen zu haben — Dragonfly kostet
    /// Cosmocredits bei der Cosmic Exploration und Scrips am Tausch. In der
    /// Spalte steht nur einer davon, und welcher, haengt an der Fundreihenfolge.
    /// Wer wissen will, ob sich der andere Weg lohnt, sah das bisher nur in der
    /// Routenvorschau.
    ///
    /// Wo bekannt, steht dabei, wer die Waehrung nimmt: Ein Betrag ohne Laden
    /// beantwortet die halbe Frage.
    /// </summary>
    private static void PricedAll(IReadOnlyList<string> prices, Vector4? colour)
    {
        var shown = Shorten(prices[0]);
        var clipped = ImGui.CalcTextSize(shown).X > ImGui.GetContentRegionAvail().X;

        Centered(shown, colour);

        if (!ImGui.IsItemHovered())
            return;

        // Ein einzelner, ungekuerzter, vollstaendig sichtbarer Preis braucht
        // keinen Hinweistext, der ihn wiederholt.
        if (prices.Count == 1 && !clipped && shown == prices[0])
            return;

        ImGui.SetTooltip(string.Join(Environment.NewLine, prices.Select(WithVendor)));
    }

    /// <summary>Wer diese Waehrung annimmt, soweit es sich sagen laesst.</summary>
    private static string WithVendor(string price)
    {
        if (!PriceTag.TryParse(price, out var tag))
            return price;

        var where = tag.FitsVendor(VendorIndex.VendorKind.GilShop) ? "gil shop"
            : tag.FitsVendor(VendorIndex.VendorKind.ScripExchange) ? "scrip exchange"
            : tag.FitsVendor(VendorIndex.VendorKind.Cosmic) ? "Cosmic Exploration"
            : null;

        return where == null ? price : $"{price}  ({where})";
    }

    /// <summary>
    /// Preis mittig, und beim Ueberfahren noch einmal ganz.
    ///
    /// Der Hinweistext traegt nach, was die Spalte nicht zeigt: den
    /// ausgeschriebenen Waehrungsnamen ebenso wie einen trotzdem noch zu
    /// langen Text.
    /// </summary>
    private static void Priced(string text, Vector4? colour)
    {
        var shown = Shorten(text);
        var clipped = ImGui.CalcTextSize(shown).X > ImGui.GetContentRegionAvail().X;

        Centered(shown, colour);

        if ((clipped || shown != text) && ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    /// <summary>
    /// Woraus sich der Bestand zusammensetzt.
    ///
    /// Eine blanke Summe verschweigt das Entscheidende: Was in der
    /// Satteltasche liegt, holt man ueberall; was bei einem Gehilfen liegt, nur
    /// an der Rufglocke. Und was nie nachgesehen wurde, ist keine Null,
    /// sondern eine Unbekannte.
    /// </summary>
    private string StockBreakdown(Restock.Row row)
    {
        var lines = new List<string> { $"{row.Bag} in your bags" };

        // Abgeschaltet heisst: kommt hier nicht vor. Wer den Schalter umgelegt
        // hat, braucht keine Zeile, die ihn daran erinnert — die Gehilfen
        // halten es genauso.
        //
        // Eingeschaltet dagegen immer eine Aussage, auch wenn nichts drin
        // liegt: Ohne sie ist "160 in your bags" nicht davon zu unterscheiden,
        // dass die Tasche nie gezaehlt wurde.
        if (_config.CountSaddlebag)
        {
            if (!row.SaddleKnown)
                lines.Add("Saddlebag not counted — the game only hands out its contents "
                          + "once it has been opened.");
            else
                lines.Add(row.Saddle > 0
                    ? $"{row.Saddle} in the saddlebag"
                        + (row.SaddleSeen is { } when ? $" ({Ago(when)})" : string.Empty)
                    : "nothing in the saddlebag");
        }

        if (_config.CountRetainers)
        {
            var holdings = _retainers.Where(row.BaitId);
            foreach (var holding in holdings)
                lines.Add($"{holding.Count} with {holding.Name} ({Ago(holding.Seen)})");

            // Kennt das Spiel die Anzahl noch nicht, ist sie null — und "0 > 0"
            // ist falsch, also stand hier gar nichts. Genau dann ist der
            // Hinweis am noetigsten.
            var (total, seen) = _retainers.Coverage();
            if (total > 0 && seen < total)
                lines.Add($"{total - seen} of {total} retainers not counted yet — "
                          + "open them once at a summoning bell.");
            else if (total == 0 && seen == 0)
                lines.Add("No retainer counted yet — open one at a summoning bell "
                          + "and what it holds counts too.");
            else if (holdings.Count == 0)
                // Alle gezaehlt und nichts gefunden ist eine Auskunft. Sie
                // wegzulassen liesse offen, ob nachgesehen wurde — genau der
                // Grund, aus dem bei der Satteltasche "nothing" dasteht.
                lines.Add("nothing with your retainers");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;

        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";

        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>Suchfeld ueber der Tabelle.</summary>
    private void DrawFilter()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##filter", "Search", ref _filter, 64);

        if (_filter.Length == 0)
            return;

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            _filter = string.Empty;

        ImGui.SameLine();
        var shown = _restock.Rows.Count(Matches);
        ImGui.TextColored(Dim, shown == 1
            ? $"1 of {_restock.Rows.Count} baits"
            : $"{shown} of {_restock.Rows.Count} baits");
    }

    private bool Matches(Restock.Row row) =>
        _filter.Length == 0 || row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Die Zeilen, wie sie gerade zu zeigen sind: gefiltert und in der
    /// Reihenfolge, die in der Kopfzeile angeklickt wurde.
    /// </summary>
    private unsafe List<Restock.Row> Visible()
    {
        var rows = _restock.Rows.Where(Matches).ToList();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount == 0)
            return rows;

        var spec = specs.Specs[0];
        var up = spec.SortDirection == ImGuiSortDirection.Ascending;

        Comparison<Restock.Row> by = spec.ColumnIndex switch
        {
            1 => (a, b) => a.Fish.Count.CompareTo(b.Fish.Count),
            2 => (a, b) => a.Have.CompareTo(b.Have),
            3 => (a, b) => a.Target.CompareTo(b.Target),
            4 => (a, b) => a.Missing.CompareTo(b.Missing),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        // Bei gleichem Wert nach Namen, sonst springen Zeilen mit gleicher
        // Fehlmenge bei jedem Bild umher.
        rows.Sort((a, b) =>
        {
            var order = by(a, b);
            if (order != 0)
                return up ? order : -order;

            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return rows;
    }

    private void DrawTable()
    {
        // Waagerechte Linien statt eines Gitters: Die senkrechten Striche
        // zerschneiden eine Zeile, die ohnehin von links nach rechts gelesen
        // wird.
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.Sortable;

        if (!ImGui.BeginTable("##baits", 7, flags))
            return;

        // Die Haendlerspalte waechst mit, statt fest zu bleiben: Bei
        // "13 vendors" plus Knopf reichten neunzig Punkte nicht, und der Knopf
        // wurde am rechten Rand abgeschnitten.
        ImGui.TableSetupColumn("Bait", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Fish", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Missing",
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort
            | ImGuiTableColumnFlags.PreferSortDescending, 74);
        // Der Preis ist "5 gil" bis "1920 gil" — mitwachsend war er meist zu
        // drei Vierteln leer. Fest, und der Platz geht an den Koedernamen.
        // Preis und Haendler bleiben unsortierbar: Der Preis kommt je nach
        // Zeile aus dem offenen Laden, den Spieldaten oder dem Marktbrett und
        // ist mal Gil, mal Scrips — eine Reihenfolge daraus waere erfunden.
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.5f);
        ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.2f);
        ImGui.TableSetupScrollFreeze(0, 1);
        CenteredHeadersRow(7, 1, 5);

        foreach (var row in Visible())
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)row.BaitId);

            ImGui.TableNextColumn();
            var ignored = row.Ignored;
            if (ImGui.Checkbox("##ign", ref ignored))
            {
                if (ignored) _config.Ignored.Add(row.BaitId);
                else _config.Ignored.Remove(row.BaitId);
                row.Ignored = ignored;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Check to skip this bait when buying.");
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Name);

            ImGui.TableNextColumn();
            if (row.Needed)
            {
                Centered(row.Fish.Count.ToString(), Dim);
                if (ImGui.IsItemHovered() && row.Fish.Count > 0)
                    ImGui.SetTooltip(string.Join("\n", row.Fish.Take(20).Select(Restock.ItemName)));
            }
            else
            {
                Centered("-", Dim);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No fish on your list needs this one.");
            }

            ImGui.TableNextColumn();
            Centered(row.Have.ToString(), row.Missing > 0 ? null : Dim);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StockBreakdown(row));

            // Zwanzig Eingabekaesten untereinander waren das lauteste in der
            // Tabelle, und ihre Zahl stand als einzige links, waehrend die
            // Nachbarspalten rechts standen. Jetzt steht hier eine Zahl wie in
            // jeder anderen Spalte, und das Feld erscheint erst beim Anklicken.
            ImGui.TableNextColumn();
            var target = row.Target;
            if (_editing == row.BaitId)
            {
                if (_grabFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _grabFocus = false;
                }

                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##target", ref target, 0, 0, "%d", ImGuiInputTextFlags.AutoSelectAll))
                {
                    target = Math.Clamp(target, 0, 9999);
                    if (target == _config.DefaultTarget) _config.Targets.Remove(row.BaitId);
                    else _config.Targets[row.BaitId] = target;
                    row.Target = target;
                    _config.Save();
                }

                if (ImGui.IsItemDeactivated())
                    _editing = 0;
            }
            else
            {
                ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0f));
                if (ImGui.Selectable($"{target}##target", false))
                {
                    _editing = row.BaitId;
                    _grabFocus = true;
                }
                ImGui.PopStyleVar();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Click to edit");
            }

            ImGui.TableNextColumn();
            // Die Fehlmenge ist die Zahl, wegen der man das Fenster oeffnet.
            Centered(row.Missing > 0 ? row.Missing.ToString() : "-",
                row.Missing > 0 ? (row.Ignored ? Dim : Wanted) : Dim);

            // Preis: was der offene Haendler verlangt, sonst der Wert aus den Spieldaten
            ImGui.TableNextColumn();
            // Im Waehrungsladen ist der Preis in Scrips, nicht in Gil. Dann lieber
            // den Text aus den Spieldaten, der die Waehrung benennt.
            if (row.InShop && row.Price > 0)
                Priced(row.Currency != 0
                    ? $"{row.Price} {Restock.ItemName(row.Currency)}"
                    : $"{row.Price} gil", null);
            else if (_vendors.PricesFor(row.BaitId) is { Count: > 0 } prices)
                PricedAll(prices, Dim);
            else if (_market.QuoteFor(row.BaitId) is { } quote)
            {
                // Vom Marktbrett, nicht aus den Spieldaten: von einem Spieler
                // gesetzt und in einer Stunde womoeglich ein anderer. Deshalb
                // gekennzeichnet und mit Alter im Hinweistext.
                Centered(quote.UnitPrice > 0 ? $"{quote.UnitPrice} gil ~" : "none listed", Dim);
                if (ImGui.IsItemHovered())
                {
                    var age = DateTime.Now - quote.When;
                    var ago = age.TotalMinutes < 1
                        ? "just now"
                        : age.TotalHours < 1
                            ? $"{(int)age.TotalMinutes} min ago"
                            : $"{(int)age.TotalHours} h ago";
                    ImGui.SetTooltip(quote.UnitPrice > 0
                        ? $"Cheapest of {quote.Listings} market board listings, seen {ago}."
                        : $"Nothing was listed when checked {ago} — or the board did not answer.");
                }
            }
            else
                Centered("—", Dim);

            ImGui.TableNextColumn();
            if (row.InShop)
            {
                if (row.Missing > 0 && !row.Ignored)
                {
                    if (ImGui.SmallButton("Buy"))
                        _queue.Start([row]);
                }
                else
                {
                    ImGui.TextDisabled("in shop");
                }
            }
            else
            {
                var vendors = _vendors.For(row.BaitId);
                if (vendors.Count > 0)
                {
                    ImGui.TextColored(Dim, vendors.Count == 1 ? "1 vendor" : $"{vendors.Count} vendors");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join(Environment.NewLine, vendors.Take(15).Select(v => $"{v} [{v.KindName}]")));

                    var best = vendors.FirstOrDefault(Reach.CanReach);
                    if (Reach.CanReach(best))
                    {
                        SameLineFarRight("Go");
                        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running || _market.Running || !_travel.Available))
                        {
                            if (ImGui.SmallButton("Go"))
                            {
                                // Cosmic Exploration hat keinen Aetheryten. Dorthin
                                // fuehrt nur der Fahrzeug-NPC, also der andere Weg.
                                if (CosmicTravel.PlanetOf(best.Zone) != null)
                                    _cosmic.StartTo(best);
                                else
                                    _travel.Start(best);
                            }
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(CosmicTravel.PlanetOf(best.Zone) != null
                                ? $"Ride to {best.Zone} and walk to {best.Npc} ({best.KindName})."
                                : $"Teleport to {best.AetheryteName} and walk to {best.Npc} ({best.KindName})."
                                             + (best.ApproximateHeight ? Environment.NewLine + "Height is estimated, the path may be off." : string.Empty));
                    }
                }
                else
                {
                    var other = _vendors.OtherSourcesFor(row.BaitId);
                    var recipe = _vendors.RecipeFor(row.BaitId);

                    // Ohne auffindbaren Haendler bleibt das Marktbrett. Ist die
                    // Option an, wird die Zeile anfahrbar statt nur beschriftet.
                    var board = _config.UseMarketBoard ? CachedBoard() : null;
                    if (board is { } destination)
                    {
                        ImGui.TextDisabled("market");
                        if (ImGui.IsItemHovered())
                        {
                            var lines = new List<string> { destination.ToString() };
                            if (recipe != null)
                                lines.Add($"Can also be crafted: {recipe}");
                            lines.AddRange(other.Take(8));
                            ImGui.SetTooltip(string.Join(Environment.NewLine, lines));
                        }

                        SameLineFarRight("Go");
                        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running
                                              || _market.Running || !_travel.Available))
                        {
                            if (ImGui.SmallButton("Go"))
                                _travel.Start(destination);
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Teleport to {destination.AetheryteName} and walk to the market board.");
                    }
                    else if (other.Count > 0)
                    {
                        ImGui.TextDisabled(other.Count == 1 ? "1 source" : $"{other.Count} sources");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(string.Join(Environment.NewLine, other.Take(10)));
                    }
                    else if (recipe != null)
                    {
                        ImGui.TextDisabled("crafted");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(recipe);
                    }
                    else
                    {
                        ImGui.TextDisabled(_vendors.Ready ? "no source" : "…");
                        if (_vendors.Ready && ImGui.IsItemHovered())
                            ImGui.SetTooltip("No shop and no recipe. Gathered, fished or market board only.");
                    }
                }
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>Sucht das naechste Marktbrett hoechstens einmal pro Sekunde.</summary>
    private VendorIndex.Vendor? CachedBoard()
    {
        var now = Environment.TickCount64;
        if (now < _boardAt)
            return _board;

        _boardAt = now + 1000;
        _board = MarketBoards.Nearest(_config, _vendors);
        return _board;
    }

    /// <summary>Plant hoechstens einmal pro Sekunde neu.</summary>
    private List<Route.RouteStop> CachedPlan()
    {
        var now = Environment.TickCount64;
        if (now < _planAt)
            return _plan;

        _planAt = now + 1000;
        _plan = _restock.Rows.Count > 0 && _vendors.Ready ? _route.Plan() : [];
        return _plan;
    }

    private void Notify(string text)
    {
        _message = text;
        _messageUntil = DateTime.Now.AddSeconds(4);
    }

    /// <summary>Gedaempft — fuer alles, was gerade nichts von einem will.</summary>
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>
    /// Honiggelb. Die eine Akzentfarbe des Fensters — fuer die Fehlmenge, den
    /// aktiven Reiter, Haken und Regler. Mehr als eine Akzentfarbe waere keine.
    /// </summary>
    private static readonly Vector4 Honey = new(1f, 0.83f, 0.36f, 1f);

    /// <summary>
    /// Ein gesaettigtes Gelb, das man abdunkelt, ist Braun — daran ist nichts
    /// zu machen. Gelb bleibt nur gelb, solange es hell ist. Deshalb tragen die
    /// hellen Toene die Farbe, und die ruhenden Flaechen sind fast neutral:
    /// Braune Knoepfe und braune Reiter waren der ganze Fehler.
    /// </summary>
    private static readonly Vector4 HoneyLit = new(0.93f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 HoneyMid = new(0.74f, 0.58f, 0.20f, 1f);

    private static readonly Vector4 HoneyDark = new(0.20f, 0.18f, 0.14f, 0.90f);
    private static readonly Vector4 HoneyFaint = new(0.15f, 0.14f, 0.11f, 0.75f);

    /// <summary>Warm — fuer die Fehlmenge, die einzige Zahl, die zum Handeln auffordert.</summary>
    private static readonly Vector4 Wanted = Honey;

    /// <summary>
    /// Setzt den Knopf ans rechte Ende der Zelle statt direkt hinter den Text.
    ///
    /// Die Beschriftungen sind verschieden lang — "market" gegen "17 vendors" —,
    /// also stand jeder Knopf woanders und das Auge musste ihn in jeder Zeile
    /// neu suchen. Am rechten Rand stehen sie in einer Flucht und die Spalte
    /// wird zu einem Ziel statt zu zwanzig.
    /// </summary>
    private static void SameLineFarRight(string label)
    {
        var width = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);

        ImGui.SameLine();
        var space = ImGui.GetContentRegionAvail().X;
        if (space > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + space - width);
    }

    /// <summary>
    /// Faerbt das Fenster honiggelb. Nur dieses Fenster und nur zwischen Pre-
    /// und PostDraw; die Farben des Spielers bleiben ueberall sonst, wie sie sind.
    /// </summary>
    private int _pushed;

    public override void PreDraw()
    {
        _pushed = 0;
        if (!_config.HoneyTheme)
            return;

        void Push(ImGuiCol which, Vector4 colour)
        {
            ImGui.PushStyleColor(which, colour);
            _pushed++;
        }

        // Ruhend: neutral dunkel. Beruehrt oder ausgewaehlt: wirklich gelb.
        Push(ImGuiCol.TitleBgActive, HoneyMid);
        Push(ImGuiCol.Tab, HoneyFaint);
        Push(ImGuiCol.TabHovered, HoneyLit);
        Push(ImGuiCol.TabActive, HoneyMid);
        Push(ImGuiCol.TabUnfocusedActive, HoneyDark);
        Push(ImGuiCol.Button, HoneyDark);
        Push(ImGuiCol.ButtonHovered, HoneyMid);
        Push(ImGuiCol.ButtonActive, HoneyLit);
        Push(ImGuiCol.Header, HoneyDark);
        Push(ImGuiCol.HeaderHovered, HoneyMid);
        Push(ImGuiCol.HeaderActive, HoneyLit);
        Push(ImGuiCol.FrameBg, HoneyFaint);
        Push(ImGuiCol.FrameBgHovered, HoneyDark);
        Push(ImGuiCol.FrameBgActive, HoneyMid);
        Push(ImGuiCol.CheckMark, Honey);
        Push(ImGuiCol.SliderGrab, HoneyMid);
        Push(ImGuiCol.SliderGrabActive, Honey);
        Push(ImGuiCol.TableHeaderBg, HoneyDark);
        Push(ImGuiCol.Separator, HoneyMid);
        Push(ImGuiCol.Border, HoneyMid);
        Push(ImGuiCol.ResizeGrip, HoneyDark);
        Push(ImGuiCol.ResizeGripHovered, HoneyMid);
        Push(ImGuiCol.ResizeGripActive, Honey);
    }

    public override void PostDraw()
    {
        if (_pushed > 0)
            ImGui.PopStyleColor(_pushed);

        _pushed = 0;
    }

    /// <summary>
    /// Die Kopfzeile von Hand, damit die Ueberschriften der Zahlenspalten
    /// mittig ueber ihren Zahlen stehen.
    ///
    /// <see cref="ImGui.TableHeadersRow"/> setzt jede Ueberschrift links an den
    /// Spaltenrand und kennt keine Ausrichtung. Bei mittigen Zahlen stand damit
    /// jede Ueberschrift neben statt ueber ihrer Spalte.
    /// </summary>
    private static void CenteredHeadersRow(int columns, int firstCentered, int lastCentered)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        for (var i = 0; i < columns; i++)
        {
            if (!ImGui.TableSetColumnIndex(i))
                continue;

            var name = ImGui.TableGetColumnName(i).ToString();
            if (i >= firstCentered && i <= lastCentered)
            {
                var width = ImGui.CalcTextSize(name).X;
                var space = ImGui.GetContentRegionAvail().X;
                if (space > width)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((space - width) * 0.5f));
            }

            ImGui.PushID(i);
            ImGui.TableHeader(name);
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Zahl mittig in der Spalte. Die Zahlenspalten sind schmal genug, dass die
    /// Ziffern damit unter ihrer Ueberschrift stehen — und alle vier stehen
    /// gleich, was vorher nicht der Fall war.
    /// </summary>
    private static void Centered(string text, Vector4? colour = null)
    {
        var width = ImGui.CalcTextSize(text).X;
        var space = ImGui.GetContentRegionAvail().X;
        if (space > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((space - width) * 0.5f));

        if (colour is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
    }

    private static IDisposable ImRaiiDisabled(bool disabled) => new DisabledScope(disabled);

    private sealed class DisabledScope : IDisposable
    {
        private readonly bool _disabled;
        public DisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (_disabled) ImGui.BeginDisabled();
        }
        public void Dispose()
        {
            if (_disabled) ImGui.EndDisabled();
        }
    }
}
