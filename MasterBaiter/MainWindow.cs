using System.Numerics;
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
    private readonly Travel _travel;
    private readonly Route _route;
    private string _message = string.Empty;
    private DateTime _messageUntil;
    private bool _wasRunning;

    public MainWindow(Configuration config, Restock restock, PurchaseQueue queue, ScripSweep sweep, MarketBoard market, CosmicTravel cosmic, VendorIndex vendors, Travel travel, Route route)
        : base("MasterBaiter###MasterBaiterMain")
    {
        _config = config;
        _restock = restock;
        _queue = queue;
        _sweep = sweep;
        _market = market;
        _cosmic = cosmic;
        _vendors = vendors;
        _travel = travel;
        _route = route;
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
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

        DrawTable();
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
        var scrip = ShopWindowReader.Kind == "scrip exchange";

        // Der Scrip-Tausch zeigt immer nur einen Reiter. Was fehlt, kann auf
        // einem anderen liegen, deshalb zaehlt hier der gesamte Bedarf und
        // nicht nur das sichtbare Blatt.
        var buyable = scrip
            ? _restock.Rows.Count(r => !r.Ignored && r.Missing > 0)
            : _restock.Rows.Count(r => r is { Ignored: false, InShop: true } && r.Missing > 0);

        if (_queue.Running || _sweep.Running)
        {
            if (ImGui.Button("Cancel"))
            {
                _sweep.Stop("Stopped.");
                _queue.Stop("Stopped.");
            }
        }
        else
        {
            using (ImRaiiDisabled(!shopOpen || buyable == 0))
            {
                if (ImGui.Button(buyable > 0 ? $"Buy missing ({buyable})" : "Buy missing"))
                {
                    if (scrip)
                        _sweep.Start();
                    else
                        _queue.Start(_restock.Rows);
                }
            }
            if (scrip && ImGui.IsItemHovered())
                ImGui.SetTooltip("Walks through every category and subcategory of the exchange and buys on each.");
        }

        if (_config.UseMarketBoard)
        {
            ImGui.SameLine();
            var marketOpen = MarketBoard.IsOpen;
            var onBoard = marketOpen ? MarketBoard.Candidates(_restock.Rows, _vendors).Count() : 0;

            if (_market.Running)
            {
                if (ImGui.Button("Stop market board"))
                    _market.Stop("Stopped.");
            }
            else
            {
                using (ImRaiiDisabled(!marketOpen || onBoard == 0))
                {
                    if (ImGui.Button(onBoard > 0 ? $"Buy on market board ({onBoard})" : "Buy on market board"))
                        _market.Start(_restock.Rows, _vendors);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Searches, waits for the listings and buys, one bait after another, " +
                                     "within your price limits.");
            }
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

        var orphans = _restock.Rows.Where(r => _vendors.For(r.BaitId).Count == 0
                                               && _vendors.OtherSourcesFor(r.BaitId).Count == 0
                                               && _vendors.RecipeFor(r.BaitId) == null).ToList();

        ImGui.TextDisabled(orphans.Count == 0
            ? $"All {_restock.Rows.Count} baits can be bought or crafted."
            : $"{orphans.Count} of {_restock.Rows.Count} baits have no shop and no recipe.");
        if (orphans.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(Environment.NewLine, orphans.Take(20).Select(r => r.Name)));
    }

    // ---------- Reiter "Options" ----------

    private void DrawOptionsTab()
    {
        Section("Stock levels");
        ImGui.TextDisabled("How much of each is worth keeping. A bait costs a few gil, a lure a few");
        ImGui.TextDisabled("thousand, so they are counted separately. The Target column overrides both.");
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

        Section("Bait list");
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
        var buyOnArrival = _config.BuyOnArrival;
        if (ImGui.Checkbox("Buy on arrival", ref buyOnArrival))
        {
            _config.BuyOnArrival = buyOnArrival;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After travelling, start buying as soon as the shop opens.");

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
        ImGui.TextDisabled("No aetheryte leads to the planets. The way is Drivingway, the Moon Rover in");
        ImGui.TextDisabled("Mare Lamentorum, which the plugin knows. Talking to a different one remembers it.");
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

        Section("Market board");
        ImGui.TextDisabled("For baits no vendor sells. Prices here are set by other players, not by the");
        ImGui.TextDisabled("game, so both limits always apply and cannot be switched off.");
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

        Section("Diagnostics");
        ImGui.TextDisabled("All three write to /xllog.");
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
              $"{Teleportable.Count} teleport destinations."
            : "Building the vendor index...");
    }

    /// <summary>Ueberschrift eines Abschnitts im Einstellungsreiter.</summary>
    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), title);
        ImGui.Spacing();
    }

    private void DrawTable()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("##baits", 7, flags))
            return;

        ImGui.TableSetupColumn("Bait", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Fish", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Inventory", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var row in _restock.Rows)
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
            ImGui.TextUnformatted(row.Fish.Count.ToString());
            if (ImGui.IsItemHovered() && row.Fish.Count > 0)
                ImGui.SetTooltip(string.Join("\n", row.Fish.Take(20).Select(Restock.ItemName)));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Have.ToString());

            ImGui.TableNextColumn();
            var target = row.Target;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##target", ref target, 0, 0))
            {
                target = Math.Clamp(target, 0, 9999);
                if (target == _config.DefaultTarget) _config.Targets.Remove(row.BaitId);
                else _config.Targets[row.BaitId] = target;
                row.Target = target;
                _config.Save();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Missing > 0 ? row.Missing.ToString() : "-");

            // Preis: was der offene Haendler verlangt, sonst der Wert aus den Spieldaten
            ImGui.TableNextColumn();
            // Im Waehrungsladen ist der Preis in Scrips, nicht in Gil. Dann lieber
            // den Text aus den Spieldaten, der die Waehrung benennt.
            if (row.InShop && row.Price > 0)
                ImGui.TextUnformatted(row.Currency != 0
                    ? $"{row.Price} {Restock.ItemName(row.Currency)}"
                    : $"{row.Price} gil");
            else if (_vendors.PriceFor(row.BaitId) is { } price)
                ImGui.TextDisabled(price);
            else if (_market.QuoteFor(row.BaitId) is { } quote)
            {
                // Vom Marktbrett, nicht aus den Spieldaten: von einem Spieler
                // gesetzt und in einer Stunde womoeglich ein anderer. Deshalb
                // gekennzeichnet und mit Alter im Hinweistext.
                ImGui.TextDisabled(quote.UnitPrice > 0 ? $"{quote.UnitPrice} gil ~" : "none listed");
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
                        : $"Nothing was listed when checked {ago}.");
                }
            }
            else
                ImGui.TextDisabled("—");

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
                    ImGui.TextDisabled(vendors.Count == 1 ? "1 vendor" : $"{vendors.Count} vendors");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join(Environment.NewLine, vendors.Take(15).Select(v => $"{v} [{v.KindName}]")));

                    var best = vendors.FirstOrDefault(Reach.CanReach);
                    if (Reach.CanReach(best))
                    {
                        ImGui.SameLine();
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

                        ImGui.SameLine();
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
