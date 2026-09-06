namespace MasterBaiter;

/// <summary>
/// Faehrt mehrere Haendler nacheinander ab, bis moeglichst wenig fehlt.
///
/// Die Auswahl ist eine gierige Ueberdeckung: Es gewinnt der Haendler, der die
/// meisten noch offenen Koeder fuehrt, dann der naechste fuer den Rest, und so
/// fort. Das ist nicht beweisbar optimal, kommt dem Optimum bei dieser
/// Groessenordnung aber sehr nahe und ist in Millisekunden gerechnet.
///
/// Haendler in der aktuellen Zone werden bei Gleichstand bevorzugt, das spart
/// einen Teleport.
/// </summary>
internal sealed class Route(Configuration config, Restock restock, VendorIndex vendors, Travel travel,
    PurchaseQueue queue, ScripSweep sweep, MarketBoard market, CosmicTravel cosmic)
{
    public sealed record RouteStop(VendorIndex.Vendor Vendor, List<string> Baits, bool IsMarketBoard = false);

    private enum State { Idle, Starting, Travelling, AtMarketBoard, Buying }

    private readonly List<RouteStop> _stops = [];
    private State _state = State.Idle;
    private int _index = -1;
    private long _nextStopAt;
    private long _shopWaitUntil;

    /// <summary>
    /// Wie lange ein Zustand dauern darf, bevor die Route ihn als haengend
    /// meldet. Ein Halt, der stillschweigend stehen bleibt, ist schlimmer als
    /// einer, der abbricht: Man wartet, ohne es zu wissen.
    /// </summary>
    private const int StateTimeoutMs = 120000;

    private long _stateSince;

    public bool Running => _state != State.Idle;
    public string Status { get; private set; } = string.Empty;
    public IReadOnlyList<RouteStop> Stops => _stops;
    public int CurrentIndex => _index;

    /// <summary>Plant, ohne zu starten. Fuer die Vorschau im Fenster.</summary>
    public List<RouteStop> Plan()
    {
        var open = new Dictionary<uint, string>();
        foreach (var row in restock.Rows)
            if (!row.Ignored && row.Missing > 0)
                open[row.BaitId] = row.Name;

        var plan = new List<RouteStop>();
        var currentZone = Plugin.ClientState.TerritoryType;

        while (open.Count > 0)
        {
            // Welcher Haendler deckt am meisten ab?
            var byVendor = new Dictionary<VendorIndex.Vendor, List<string>>();
            foreach (var (baitId, name) in open)
            foreach (var vendor in vendors.For(baitId))
            {
                // Nicht nur Teleportziele: Die Planeten der Cosmic
                // Exploration sind ueber den Fahrzeug-NPC erreichbar.
                if (!Reach.CanReach(vendor))
                    continue;
                if (!byVendor.TryGetValue(vendor, out var list))
                    byVendor[vendor] = list = [];
                list.Add(name);
            }

            if (byVendor.Count == 0)
                break;

            var best = byVendor
                .OrderByDescending(e => e.Value.Count)
                .ThenBy(e => e.Key.Kind)                       // Gil vor Cosmic vor Scrip
                .ThenByDescending(e => e.Key.PreferredScripHub) // unter Scrips: Idyllshire
                .ThenByDescending(e => e.Key.Territory == currentZone)
                .ThenBy(e => e.Key.ApproximateHeight)
                .First();

            plan.Add(new RouteStop(best.Key, best.Value.OrderBy(n => n).ToList()));
            currentZone = best.Key.Territory;

            foreach (var baitId in open.Where(kv => best.Value.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                open.Remove(baitId);

            if (plan.Count >= 12) // Notbremse gegen ausufernde Routen
                break;
        }

        // Was kein Haendler fuehrt, bleibt uebrig. Ist das Marktbrett erlaubt,
        // wird es der letzte Halt — zuletzt, weil es das einzige ist, was Gil zu
        // fremden Preisen kostet.
        if (config.UseMarketBoard && open.Count > 0 && MarketBoards.Nearest(config, vendors) is { } board)
            plan.Add(new RouteStop(board, open.Values.OrderBy(n => n).ToList(), true));

        return plan;
    }

    public void Start()
    {
        _stops.Clear();
        _stops.AddRange(Plan());

        if (_stops.Count == 0)
        {
            Status = "Nothing to buy, or no reachable vendor.";
            Enter(State.Idle);
            return;
        }

        Plugin.Log.Information($"[MasterBaiter] Route with {_stops.Count} stops: " +
                               string.Join(" -> ", _stops.Select(s => $"{s.Vendor.Npc} ({s.Baits.Count})")));
        _index = -1;
        Enter(State.Starting);
        Advance();
    }

    public void Stop(string reason)
    {
        if (travel.Running)
            travel.Stop(reason);
        if (cosmic.Running)
            cosmic.Stop(reason);
        if (sweep.Running)
            sweep.Stop(reason);
        if (market.Running)
            market.Stop(reason);
        if (queue.Running)
            queue.Stop(reason);
        Enter(State.Idle);
        _stops.Clear();
        _index = -1;
        Status = reason;
    }

    private void Advance()
    {
        _index++;
        if (_index >= _stops.Count)
        {
            Status = $"Route finished, {_stops.Count} stops.";
            Plugin.Log.Information($"[MasterBaiter] {Status}");
            Enter(State.Idle);
            return;
        }

        // Ein offenes Ladenfenster verhindert den naechsten Teleport. Es zu
        // schliessen genuegt aber nicht — geschieht das im selben Frame wie der
        // Teleport, lehnt Lifestream noch ab. Deshalb wird der Halt hier nur
        // vorgemerkt und erst nach der Pause gestartet.
        if (ShopWindowReader.IsOpen)
            ShopWindowReader.Close();

        _nextStopAt = Pacing.Next(1100);
        _shopWaitUntil = 0;
        var stop = _stops[_index];
        Status = $"Stop {_index + 1}/{_stops.Count}: {stop.Vendor.Npc} in {stop.Vendor.Zone}";
        Plugin.Log.Information($"[MasterBaiter] {Status}");
        Enter(State.Starting);
    }

    /// <summary>Wechselt den Zustand und haelt fest, seit wann er gilt.</summary>
    private void Enter(State next)
    {
        if (_state != next)
            Plugin.Log.Debug($"[MasterBaiter] Route: {_state} -> {next}");
        _state = next;
        _stateSince = Environment.TickCount64;
    }

    public void Tick()
    {
        // Notbremse: Bleibt ein Zustand haengen, sagt die Route es, statt
        // stumm stehen zu bleiben. Der Kaufzustand ist ausgenommen, solange
        // tatsaechlich gekauft wird — ein Scrip-Durchlauf dauert seine Zeit.
        if (_state != State.Idle && _stateSince != 0
            && Environment.TickCount64 - _stateSince > StateTimeoutMs
            && !queue.Running && !sweep.Running && !market.Running
            && !travel.Running && !cosmic.Running)
        {
            Plugin.Log.Warning(
                $"[MasterBaiter] Route stuck in {_state} at stop {_index + 1}/{_stops.Count} " +
                $"for {(Environment.TickCount64 - _stateSince) / 1000}s. Moving on.");
            Advance();
            return;
        }

        // Ein gescheiterter Halt darf nicht im selben Frame zum naechsten
        // fuehren, sonst rauscht eine ganze Route in einer Millisekunde durch
        // und im Log steht nur "fertig".
        if (Environment.TickCount64 < _nextStopAt)
            return;

        switch (_state)
        {
            case State.Starting:
            {
                var stop = _stops[_index];

                // Zu den Planeten fuehrt kein Teleport, sondern der Fahrzeug-NPC.
                if (Reach.IsCosmic(stop.Vendor))
                    cosmic.StartTo(stop.Vendor);
                else
                    travel.Start(stop.Vendor);

                // Am Marktbrett oeffnet sich kein Ladenfenster, deshalb ein
                // eigener Zustand — sonst gilt der Halt als gescheitert, obwohl
                // er geklappt hat.
                Enter(stop.IsMarketBoard ? State.AtMarketBoard : State.Travelling);
                return;
            }

            case State.Travelling:
                // Eine Cosmic-Fahrt endet damit, dass sie selbst eine normale
                // Reise zum Haendler startet; beide muessen still sein.
                if (travel.Running || cosmic.Running)
                    return;

                if (!ShopWindowReader.IsOpen)
                {
                    Plugin.Log.Warning($"[MasterBaiter] Stop {_index + 1} failed: {travel.Status}");
                    Advance();
                    return;
                }

                if (_shopWaitUntil == 0)
                    _shopWaitUntil = Environment.TickCount64 + 6000;

                // Erst kaufen, wenn der Laden seine Posten zeigt. Direkt nach
                // dem Oeffnen ist das Fenster noch leer.
                //
                // Ausgenommen der Scrip-Tausch: Der oeffnet auf irgendeinem
                // Reiter, und der kann leer sein — beim letzten Lauf war es
                // "Music/Furnishings/Misc.". Dort auf Eintraege zu warten heisst
                // auf nichts zu warten; der Durchlauf blaettert ja selbst.
                if (ShopWindowReader.Kind != "scrip exchange"
                    && ShopWindowReader.ReadEntries().Count == 0)
                {
                    if (Environment.TickCount64 < _shopWaitUntil)
                        return;
                    Plugin.Log.Warning($"[MasterBaiter] Stop {_index + 1}: the shop never listed anything.");
                    Advance();
                    return;
                }

                restock.Refresh();
                // Am Scrip-Tausch liegen die Koeder ueber mehrere Reiter
                // verteilt; ein Kauflauf auf dem offenen Blatt faende nur einen Teil.
                if (ShopWindowReader.Kind == "scrip exchange")
                    sweep.Start();
                else
                    queue.Start(restock.Rows);
                Enter(State.Buying);
                break;

            case State.AtMarketBoard:
                if (travel.Running || cosmic.Running)
                    return;

                if (!MarketBoard.IsOpen)
                {
                    Plugin.Log.Warning($"[MasterBaiter] Stop {_index + 1} failed: {travel.Status}");
                    Advance();
                    return;
                }

                restock.Refresh();
                market.Start(restock.Rows, vendors);
                Enter(State.Buying);
                break;

            case State.Buying:
                if (queue.Running || sweep.Running || market.Running)
                    return;
                Advance();
                break;
        }
    }
}
