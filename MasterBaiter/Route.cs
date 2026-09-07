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

    /// <summary>Plant, ohne zu starten. Fuer die Vorschau im Fenster.</summary>
    public List<RouteStop> Plan()
    {
        var open = new Dictionary<uint, string>();
        var missing = new Dictionary<uint, int>();
        foreach (var row in restock.Rows)
            if (!row.Ignored && row.Missing > 0)
            {
                open[row.BaitId] = row.Name;
                missing[row.BaitId] = row.Missing;
            }

        var plan = new List<RouteStop>();
        var currentZone = Plugin.ClientState.TerritoryType;

        // Was im Beutel liegt, waehrend der Planung mitgefuehrt: Zwei Staende,
        // die dieselben Scrips nehmen, teilen sich einen Vorrat. Wer das nicht
        // mitrechnet, plant den zweiten Halt fuer Geld ein, das der erste
        // schon ausgegeben hat.
        //
        // Eine unbekannte Waehrung gilt als unbegrenzt: Nichtwissen darf keinen
        // Haendler ausschliessen.
        var purse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Balance(string currency)
        {
            if (!purse.TryGetValue(currency, out var left))
                purse[currency] = left = Wallet.Balance(currency) ?? int.MaxValue;

            return left;
        }

        while (open.Count > 0)
        {
            // Welcher Haendler deckt am meisten ab?
            var byVendor = new Dictionary<VendorIndex.Vendor, List<string>>();
            var candidates = new Dictionary<VendorIndex.Vendor, List<uint>>();

            foreach (var (baitId, _) in open)
            foreach (var vendor in vendors.For(baitId))
            {
                // Nicht nur Teleportziele: Die Planeten der Cosmic
                // Exploration sind ueber den Fahrzeug-NPC erreichbar.
                if (!Reach.CanReach(vendor))
                    continue;
                if (!candidates.TryGetValue(vendor, out var ids))
                    candidates[vendor] = ids = [];
                ids.Add(baitId);
            }

            // Was ist dort mit dem bezahlbar, was gerade da ist?
            foreach (var (vendor, ids) in candidates)
            {
                var payable = Affordable(vendor, ids, missing, Balance);
                if (payable.Count == 0)
                    continue;

                byVendor[vendor] = payable.Select(id => open[id]).ToList();
            }

            if (byVendor.Count == 0)
                break;

            // Ein Halt im Gebiet, in dem man ohnehin schon steht, spart eine
            // ganze Reise. Er darf deshalb einen Koeder weniger abdecken als
            // ein entfernter und trotzdem gewinnen — ohne diesen Ausgleich
            // fliegt die Route wegen eines einzigen Postens um die Welt.
            const int sameZoneWorth = 1;

            var best = byVendor
                .OrderByDescending(e => e.Value.Count
                                        + (e.Key.Territory == currentZone ? sameZoneWorth : 0))
                .ThenBy(e => e.Key.Kind)                       // Gil vor Cosmic vor Scrip
                .ThenByDescending(e => e.Key.PreferredScripHub) // unter Scrips: Idyllshire
                .ThenByDescending(e => e.Key.Territory == currentZone)
                .ThenBy(e => e.Key.ApproximateHeight)
                .First();

            plan.Add(new RouteStop(best.Key, best.Value.OrderBy(n => n).ToList()));
            currentZone = best.Key.Territory;

            // Was dieser Halt kostet, steht dem naechsten nicht mehr zur
            // Verfuegung.
            foreach (var baitId in candidates[best.Key])
            {
                if (!best.Value.Contains(open[baitId]))
                    continue;
                if (PriceAt(best.Key, baitId) is not { } tag)
                    continue;

                var spent = (int)Math.Min(int.MaxValue, tag.TotalFor(missing.GetValueOrDefault(baitId)));
                purse[tag.Currency] = Math.Max(0, Balance(tag.Currency) - spent);
            }

            foreach (var baitId in open.Where(kv => best.Value.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                open.Remove(baitId);

            if (plan.Count >= 12) // Notbremse gegen ausufernde Routen
                break;
        }

        // Was kein Haendler fuehrt, bleibt uebrig. Ist das Marktbrett erlaubt,
        // wird es der letzte Halt — zuletzt, weil es das einzige ist, was Gil zu
        // fremden Preisen kostet.
        if (config.UseMarketBoard && open.Count > 0
            && MarketBoards.Nearest(config, vendors, currentZone) is { } board)
            plan.Add(new RouteStop(board, open.Values.OrderBy(n => n).ToList(), true));

        return plan;
    }

    /// <summary>
    /// Der Preis dieses Koeders in der Waehrung, die dieser Laden nimmt.
    ///
    /// Null heisst: unbekannt. Das ist keine Aussage ueber Bezahlbarkeit —
    /// wer aus fehlendem Wissen "zu teuer" folgert, streicht Haendler, an
    /// denen alles in Ordnung waere.
    /// </summary>
    private PriceTag? PriceAt(VendorIndex.Vendor vendor, uint baitId)
    {
        foreach (var text in vendors.PricesFor(baitId))
            if (PriceTag.TryParse(text, out var tag) && tag.FitsVendor(vendor.Kind))
                return tag;

        return null;
    }

    /// <summary>
    /// Welche dieser Koeder man an diesem Laden bezahlen kann.
    ///
    /// Koeder ohne bekannten Preis zaehlen mit: Sie sind der Regelfall bei
    /// Gil-Haendlern, und sie auszuschliessen hiesse, wegen eines fehlenden
    /// Datensatzes nicht hinzufahren.
    /// </summary>
    private List<uint> Affordable(VendorIndex.Vendor vendor, List<uint> baitIds, Dictionary<uint, int> missing,
        Func<string, int> balance)
    {
        var unpriced = new List<uint>();
        var priced = new Dictionary<string, List<Purse.Wanted>>(StringComparer.OrdinalIgnoreCase);

        foreach (var baitId in baitIds)
        {
            if (PriceAt(vendor, baitId) is not { } tag)
            {
                unpriced.Add(baitId);
                continue;
            }

            if (!priced.TryGetValue(tag.Currency, out var list))
                priced[tag.Currency] = list = [];

            list.Add(new Purse.Wanted(baitId, missing.GetValueOrDefault(baitId), tag));
        }

        var payable = new List<uint>(unpriced);

        foreach (var (currency, wanted) in priced)
        {
            // Der groesste Bedarf zuerst — was am meisten fehlt, soll zuerst
            // vom Vorrat bedient werden.
            var ordered = wanted.OrderByDescending(w => w.Amount).ToList();
            payable.AddRange(Purse.Payable(ordered, balance(currency)));
        }

        return payable;
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
            ChatReport.Say(config, $"Route finished, {_stops.Count} stops.");
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
                    ChatReport.Warn(config, $"Stop {_index + 1} failed: {travel.Status}");
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
                    ChatReport.Warn(config, $"Stop {_index + 1} failed: {travel.Status}");
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
