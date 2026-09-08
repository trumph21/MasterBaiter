namespace MasterBaiter;

/// <summary>
/// Die eine Stelle, die jeden Frame nachsieht, was sich geaendert hat.
///
/// Gebaut als Beobachter und nicht als Aenderung an den Maschinen selbst: Wer
/// in acht Klassen je ein Dutzend Protokollzeilen verteilt, hat danach acht
/// Klassen, die zur Haelfte aus Protokoll bestehen — und uebersieht trotzdem
/// den Uebergang, den niemand ausgeschrieben hat. Hier steht der Zustand
/// jeder Maschine einmal, und <see cref="Trace.Change"/> entscheidet, was davon
/// eine Zeile wert ist.
///
/// Damit ist die Spur vollstaendig, ohne dass eine einzige Maschine davon weiss.
/// </summary>
internal sealed class Watch(
    Configuration config,
    Restock restock,
    Travel travel,
    Route route,
    PurchaseQueue queue,
    ScripSweep sweep,
    MarketBoard market,
    StashTransfer stash,
    RetainerVisit visit,
    RetainerRun retainerRun,
    CosmicTravel cosmic)
{
    /// <summary>
    /// Wie oft die Fenster abgefragt werden.
    ///
    /// Sie zu erfragen kostet je einen Griff in die Addon-Liste. Viermal die
    /// Sekunde ist fein genug, um jeden Uebergang zu sehen, den ein Mensch
    /// ausloest, und grob genug, um im Bild nicht aufzufallen.
    /// </summary>
    private const int WindowIntervalMs = 250;

    /// <summary>Wie oft der Geldbeutel nachgerechnet wird.</summary>
    private const int PurseIntervalMs = 5000;

    private long _nextWindows;
    private long _nextPurse;

    public void Tick()
    {
        // Zuerst die Maschinen: Ihr Zustand steht ohnehin im Speicher, das
        // Nachsehen kostet nichts.
        Trace.Change("Travel", travel.Running ? $"running - {travel.Status}" : "idle");
        Trace.Change("Route", route.Running ? $"running - {route.Status}" : "idle");
        Trace.Change("Purchases", queue.Running ? $"running - {queue.Status}" : "idle");
        Trace.Change("Scrip sweep", sweep.Running ? $"running - {sweep.Status}" : "idle");
        Trace.Change("Market board run", market.Running ? $"running - {market.Status}" : "idle");
        Trace.Change("Stash transfer", stash.Running ? $"running - {stash.Status}" : "idle");
        Trace.Change("Retainer visit", visit.Running ? $"running - {visit.Status}" : "idle");
        Trace.Change("Sort run", retainerRun.Running ? $"running - {retainerRun.Status}" : "idle");
        Trace.Change("Cosmic trip", cosmic.Running ? $"running - {cosmic.Status}" : "idle");

        // Und der Ort. Ein Gebietswechsel erklaert die Haelfte aller Zeilen,
        // die danach kommen, und stand bisher nirgends.
        Trace.Change("Territory", Plugin.ClientState.TerritoryType.ToString());

        var now = Environment.TickCount64;
        if (now < _nextWindows)
            return;
        _nextWindows = now + WindowIntervalMs;

        // Die Fenster, an denen dieses Plugin haengt. Ob eines offen ist,
        // entscheidet ueber fast jeden Schritt — und "es hat nicht geoeffnet"
        // war schon zweimal die falsche Erklaerung fuer etwas anderes.
        Trace.Change("Shop window", ShopWindowReader.IsOpen ? "open" : "closed");
        Trace.Change("Market board window", MarketBoard.IsOpen ? "open" : "closed");
        Trace.Change("Retainer list", RetainerList.IsOpen ? "open" : "closed");
        Trace.Change("Saddlebag window", Stash.Saddlebag.Ready ? "open" : "closed");
        Trace.Change("Retainer bag window", Stash.Retainer.Ready ? "open" : "closed");
        Trace.Change("Talk dialogue", TalkDialog.IsOpen ? "open" : "closed");
        Trace.Change("Menu dialogue", TopicSelect.IsOpen ? "open" : "closed");

        // Wie viel Platz noch ist: Ein volles Inventar ist der Grund, aus dem
        // ein Kauf lautlos nichts tut.
        Trace.Change("Free bag slots", FreeBagSlots().ToString());

        // Der Geldbeutel seltener, weil er teurer ist: Jede Abfrage zerlegt
        // saemtliche Preistexte. Waehrungen aendern sich ohnehin nur, wenn
        // etwas gekauft wurde.
        if (now < _nextPurse || Vendors is not { Ready: true } vendors)
            return;
        _nextPurse = now + PurseIntervalMs;

        foreach (var holding in Purse.Held(restock.Rows.SelectMany(r => vendors.PricesFor(r.BaitId))))
            Trace.Change($"Purse {holding.Currency}",
                $"{holding.Amount}{(holding.Live ? string.Empty : " (remembered)")}");

        // Und die Einstellungen als Ganzes: So meldet sich jede Aenderung, ohne
        // dass an jedem einzelnen Haken eine Protokollzeile haengt.
        Trace.Change("Settings", Describe());
    }

    /// <summary>
    /// Der Haendlerindex, sobald es ihn gibt. Vor dem Aufbau steht hier null,
    /// und dann bleibt die Geldbeutel-Spur eben aus — sie ist kein Grund, den
    /// Start zu verzoegern.
    /// </summary>
    public VendorIndex? Vendors { get; set; }

    private static unsafe int FreeBagSlots()
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (inv == null)
            return 0;

        var free = 0;
        foreach (var type in Restock.Bags)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (var i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>
    /// Schreibt einmal auf, womit das Plugin arbeitet.
    ///
    /// Gehoert an den Anfang eines Protokolls: Die Haelfte aller Rueckfragen
    /// zu einem Fehlerbericht sind Fragen nach Einstellungen.
    /// </summary>
    public void Settings() => Plugin.Log.Information($"[MasterBaiter] Settings: {Describe()}");

    /// <summary>
    /// Alle Einstellungen in einer Zeile.
    ///
    /// Wird auch als Zustand beobachtet, und damit meldet sich jede Aenderung
    /// von selbst — ohne dass an zwanzig Haken je eine Protokollzeile haengt,
    /// von denen die einundzwanzigste dann fehlt.
    /// </summary>
    private string Describe() =>
        $"bait target {config.DefaultTarget}, " +
            $"lure target {config.DefaultLureTarget}, " +
            $"saddlebag counted {config.CountSaddlebag}, retainers counted {config.CountRetainers}, " +
            $"market board {config.UseMarketBoard} (max {config.MarketMaxUnitPrice}/item, " +
            $"{config.MarketMaxGilPerRun}/run), mount {config.UseMount}, flight {config.UseFlight}, " +
            $"sprint {config.UseSprint}, pacing {config.PacingPercent}%, " +
            $"buy on arrival {config.BuyOnArrival}, all tackle {config.ShowAllTackle}, " +
            $"bell {Where(config.PreferredBellTerritory)}, " +
            $"market {Where(config.PreferredMarketTerritory)}, " +
            $"detailed log {config.DetailedLog}";

    private static string Where(uint territory) => territory == 0 ? "nearest" : territory.ToString();
}
