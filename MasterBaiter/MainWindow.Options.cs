using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MasterBaiter;

/// <summary>
/// Der Reiter "Options".
///
/// Teil von <see cref="MainWindow"/>. Die Datei war auf zweitausend Zeilen
/// gewachsen und enthielt vier Reiter, die Vorschau und ein Dutzend Helfer —
/// zum Nachschlagen zu viel auf einmal. Aufgeteilt, nicht umgebaut: Es ist
/// dieselbe Klasse, nur in lesbaren Stuecken.
/// </summary>
internal sealed partial class MainWindow
{
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

        var keepOcean = _config.KeepOceanBait;
        if (ImGui.Checkbox("Keep Ocean Fishing bait", ref keepOcean))
        {
            _config.KeepOceanBait = keepOcean;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                $"{OceanFishing.Names} stay in your bags." + Environment.NewLine +
                "Sort Bait Storage treats them as needed and never puts them away." +
                Environment.NewLine +
                "Ocean Fishing is not in the gather list — what bites there depends on route, " +
                "time and weather —" + Environment.NewLine +
                "so without this they look like ballast and go to a retainer. Three stacks cost " +
                "three slots;" + Environment.NewLine +
                "missing one two minutes before the ferry costs the trip.");

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

            DrawMarketChoice();
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

        var useMount = _config.UseMount;
        if (ImGui.Checkbox("Use a mount", ref useMount))
        {
            _config.UseMount = useMount;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Calls a mount when the way is long enough to be worth the two seconds it takes." +
                Environment.NewLine +
                "Whether mounting is allowed here is the game's answer, not a list of mine: " +
                "in cities" + Environment.NewLine +
                "and instances it refuses, and then the character walks.");

        using (ImRaiiDisabled(!_config.UseMount))
        {
            var useFlight = _config.UseFlight;
            if (ImGui.Checkbox("Fly where you can", ref useFlight))
            {
                _config.UseFlight = useFlight;
                _config.Save();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(
                "Flies in zones whose aether currents you have collected, which makes the route " +
                "a line" + Environment.NewLine +
                "instead of a walk around the scenery. Everywhere else it stays on the ground." +
                Environment.NewLine +
                "Needs a mount, so it follows the switch above." + Environment.NewLine +
                "Off by default: the check is sound but untested, and a flight path in a zone " +
                "you cannot" + Environment.NewLine +
                "fly in ends with the character standing underneath its destination.");

        DrawBellChoice();

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
            // Ein grauer Knopf ohne Hinweistext sagt nur, dass er nicht geht.
            // Der Grund ist hier nicht zu erraten: Der Fahrzeug-NPC steht in
            // keiner Tabelle und muss einmal angesprochen worden sein.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!_cosmic.GateKnown
                    ? "The Moon Rover NPC is not known yet." + Environment.NewLine +
                      "Talk to Drivingway in Mare Lamentorum once and the way is remembered."
                    : !_travel.Available
                        ? "vnavmesh or Lifestream is missing."
                        : $"Teleport to Bestways Burrow and ride to {_config.CosmicPlanet}.");
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

    /// <summary>Ueberschrift eines Abschnitts im Einstellungsreiter.</summary>
    /// <summary>
    /// An welche Rufglocke gefahren wird.
    ///
    /// Zur Auswahl steht nur, was gelernt wurde. Ein Gebiet fest einzutragen,
    /// das der Spieler noch nie besucht hat, hiesse seine Koordinaten zu raten,
    /// und eine geratene Position schickt den Charakter an eine Wand — dieselbe
    /// Regel wie bei den Glocken selbst.
    /// </summary>
    private void DrawBellChoice() =>
        DrawCityChoice("Summoning bell", SummoningBells.All(_config, _vendors),
            _config.PreferredBellTerritory,
            city =>
            {
                _config.PreferredBellTerritory = city;
                _config.Save();
            },
            "Which bell the retainer run travels to.",
            "No summoning bell known yet. Walk past one and it appears here.");

    /// <summary>
    /// In welcher Stadt am Marktbrett gekauft wird.
    ///
    /// Anders als bei den Glocken ist hier fast alles schon bekannt: Die
    /// Bretter der Stadtgebiete stehen in einer mitgelieferten Tabelle, offline
    /// aus den Kartendateien gelesen. Zur Auswahl steht also mehr, als man je
    /// besucht hat.
    /// </summary>
    private void DrawMarketChoice() =>
        DrawCityChoice("Market board city", MarketBoards.All(_config, _vendors),
            _config.PreferredMarketTerritory,
            city =>
            {
                _config.PreferredMarketTerritory = city;
                _config.Save();
            },
            "Which city the market board stop uses." + Environment.NewLine +
            "Which of its boards is up to the distance — Limsa has six, and walking across town " +
            "is no gain.",
            "No market board known yet.");

    /// <summary>
    /// Eine Stadt aus einer Liste von Zielen waehlen, oder "die naechste".
    ///
    /// Ueber das Gebiet und nicht ueber den einzelnen Punkt: Sechs Eintraege,
    /// die alle "Limsa Lominsa Lower Decks" heissen, waeren keine Auswahl.
    /// </summary>
    private void DrawCityChoice(string label, List<VendorIndex.Vendor> spots, uint current,
        Action<uint> choose, string what, string empty)
    {
        // Nach Gebiet zusammengefasst, in der Reihenfolge, in der sie kommen.
        var cities = new List<(uint Territory, string Zone)>();
        foreach (var spot in spots)
            if (spot.Zone.Length > 0 && cities.All(c => c.Territory != spot.Territory))
                cities.Add((spot.Territory, spot.Zone));

        cities.Sort((a, b) => string.CompareOrdinal(a.Zone, b.Zone));

        var chosen = cities.FirstOrDefault(c => c.Territory == current);
        var preview = chosen.Territory != 0 ? chosen.Zone : "Nearest one";

        ImGui.SetNextItemWidth(260);
        using (var combo = ImRaiiCombo(label, preview))
        {
            if (combo.Open)
            {
                if (ImGui.Selectable("Nearest one", current == 0))
                    choose(0);

                foreach (var city in cities)
                    if (ImGui.Selectable(city.Zone, city.Territory == current))
                        choose(city.Territory);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cities.Count == 0
                ? empty
                : what + Environment.NewLine +
                  "\"Nearest one\" picks by distance, so it changes with where you are." +
                  Environment.NewLine +
                  "Pick a city and it is always that one — unless nothing there can be reached, " +
                  "and then the nearest wins after all." + Environment.NewLine +
                  $"{cities.Count} cities to choose from.");
    }

    private static ComboScope ImRaiiCombo(string label, string preview) => new(label, preview);

    private readonly struct ComboScope : IDisposable
    {
        public bool Open { get; }
        public ComboScope(string label, string preview) => Open = ImGui.BeginCombo(label, preview);
        public void Dispose()
        {
            if (Open) ImGui.EndCombo();
        }
    }

    private void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(_config.HoneyTheme ? Honey : new Vector4(0.6f, 0.8f, 1f, 1f), title);
        ImGui.Spacing();
    }
}
