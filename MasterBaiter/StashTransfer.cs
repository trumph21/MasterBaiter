using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MasterBaiter;

/// <summary>
/// Ein Lager ausserhalb des Beutels: die Satteltasche oder ein Gehilfe.
///
/// Beide verhalten sich gleich — Faecher, die nur bei offenem Fenster wirklich
/// da sind, und ganze Stapel als kleinste Einheit. Sie unterscheiden sich in
/// drei Punkten, und nur die stehen hier: welche Container, woran man das
/// offene Fenster erkennt, und ob es sich von selbst oeffnen laesst.
/// </summary>
internal sealed unsafe class Stash
{
    private static readonly InventoryType[] BasicSaddlebags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
    ];

    private static readonly InventoryType[] AllSaddlebags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>Hauptkommando 77 im MainCommand-Blatt: "Chocobo Saddlebag".</summary>
    private const uint SaddlebagCommand = 77;

    public static readonly Stash Saddlebag = new()
    {
        Name = "Saddlebag",
        Into = "in the saddlebag",
        OutOf = "from the saddlebag",
        Addons = ["InventoryBuddy"],
        CanOpenItself = true,
    };

    public static readonly Stash Retainer = new()
    {
        Name = "Retainer",
        Into = "with the retainer",
        OutOf = "from the retainer",
        Addons = ["InventoryRetainer", "InventoryRetainerLarge"],
        CanOpenItself = false,
    };

    public string Name { get; private init; } = string.Empty;
    public string Into { get; private init; } = string.Empty;
    public string OutOf { get; private init; } = string.Empty;

    /// <summary>Laesst sich das Fenster ohne den Spieler oeffnen?</summary>
    public bool CanOpenItself { get; private init; }

    private string[] Addons { get; init; } = [];

    private bool IsSaddlebag => ReferenceEquals(this, Saddlebag);

    /// <summary>
    /// Die Faecher, die dieser Charakter wirklich hat.
    ///
    /// Ohne Abonnement gibt es die Premium-Haelfte der Satteltasche nicht, ihre
    /// Container melden sich aber trotzdem — mit siebzig leeren Faechern. Die
    /// sahen aus wie siebzig freie Plaetze, und deshalb wurden siebenundzwanzig
    /// Zuege in eine Tasche eingereiht, in der genau einer Platz hatte.
    /// </summary>
    public InventoryType[] Containers
    {
        get
        {
            if (!IsSaddlebag)
                return RetainerPages;

            var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            return state != null && state->HasPremiumSaddlebag ? AllSaddlebags : BasicSaddlebags;
        }
    }

    /// <summary>
    /// Ist das Lager wirklich offen?
    ///
    /// Gefragt wird nach dem Fenster, nicht nach <c>IsLoaded</c> der Faecher.
    /// Das war der Fehler, der zwei Verbindungsabbrueche gekostet hat: Bei
    /// geschlossenem Fenster meldet das Spiel die Faecher als geladen, gibt
    /// aber nur leere zurueck. Jeder Zug dorthin wurde vom Server abgelehnt.
    ///
    /// Ein leeres Fach ist nur dann wirklich frei, wenn jemand hingesehen hat.
    /// </summary>
    public bool Ready
    {
        get
        {
            foreach (var name in Addons)
            {
                var ptr = Plugin.GameGui.GetAddonByName(name);
                if (ptr.IsNull || !ptr.IsVisible)
                    continue;

                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
                if (addon->IsReady)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Oeffnet das Fenster, sofern das ohne den Spieler geht.
    ///
    /// Die Satteltasche geht ueber das Hauptkommando des Spiels auf. Ein
    /// Gehilfe nicht: Dorthin fuehrt nur die Rufglocke, und die anzufahren ist
    /// eine eigene Reise mit eigenen Fehlerquellen. Hier gibt es dann nichts zu
    /// tun ausser ehrlich zu sein.
    /// </summary>
    public void Open()
    {
        if (!CanOpenItself)
            return;

        var ui = UIModule.Instance();
        if (ui == null)
        {
            Plugin.Log.Warning($"[MasterBaiter] {Name}: could not reach the window.");
            return;
        }

        ui->ExecuteMainCommand(SaddlebagCommand);
    }

    /// <summary>Was beim letzten Blick von diesem Koeder dort lag.</summary>
    public int Remembered(Restock.Row row) => IsSaddlebag ? row.Saddle : row.Retainer;
}

/// <summary>
/// Schiebt Koeder zwischen Beutel und einem Lager hin und her.
///
/// Zwei Richtungen mit verschiedenen Regeln:
///
///   Holen    fuellt den Beutel bis zur Zielmenge auf. Ist er leer, wird ein
///            zu grosser Stapel trotzdem geholt — sonst hat man nichts zum
///            Angeln.
///   Wegraeumen nimmt nur, was kein Fisch der Liste mehr braucht. Ein
///            Ueberschuss ueber die Zielmenge ist kein Grund; der gehoert dem
///            Spieler.
///
/// Beides nur auf Zuruf, nie von selbst: Liefe das Wegraeumen automatisch,
/// schoebe es sofort zurueck, was das Holen gerade herausgesucht hat.
///
/// Und beides langsam. Ein Gegenstandszug ist ein Paket an den Server; zwei
/// Verbindungsabbrueche haben gezeigt, was ein Schwung abgelehnter Pakete
/// bewirkt.
/// </summary>
internal sealed unsafe class StashTransfer(Configuration config)
{
    private sealed record Move(uint BaitId, string Name, InventoryType From, ushort Slot, int Amount,
        bool IntoStash);

    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>
    /// Abstand zwischen zwei Bewegungen.
    ///
    /// Deutlich langsamer als beim Kaufen, und das aus Erfahrung: Mit 300 ms
    /// wurden siebenundzwanzig Zuege in sieben Sekunden abgefeuert, davon
    /// sechsundzwanzig vom Spiel abgelehnt — und die Verbindung brach ab.
    ///
    /// Absichtlich nicht ueber <see cref="Pacing"/> skaliert: Wer die
    /// Geschwindigkeit hochdreht, soll damit nicht die Grenze verschieben, die
    /// hier aus einem Zwischenfall stammt.
    /// </summary>
    private const int DelayMs = 1200;

    /// <summary>
    /// So viele abgelehnte Zuege hintereinander beenden den Durchlauf.
    ///
    /// Was dreimal nicht geht, geht auch beim siebenundzwanzigsten Mal nicht.
    /// Weiterzumachen hiesse, gegen eine geschlossene Tuer zu klopfen — mit
    /// jedem Klopfen ein Paket.
    /// </summary>
    private const int MaxFailures = 3;

    /// <summary>So lange wird auf die Faecher gewartet, nachdem geoeffnet wurde.</summary>
    private const int OpenTimeoutMs = 5000;

    private readonly List<Move> _queue = [];
    private long _nextAt;
    private int _moved;
    private int _shifted;
    private int _failures;
    private string _lastName = string.Empty;
    private bool _stowing;
    private Restock? _restock;
    private long _openUntil;
    private Stash _stash = Stash.Saddlebag;

    public bool Running { get; private set; }
    public string Status { get; private set; } = string.Empty;

    // Was seit dem letzten Abholen bewegt wurde. Der Gang ueber mehrere Lager
    // zaehlt daraus seine Gesamtzahl zusammen.
    private int _sinceFetched;
    private int _sinceStowed;

    /// <summary>
    /// Gibt heraus, was seither bewegt wurde, und setzt die Zaehlung zurueck.
    ///
    /// Abholen statt Ablesen, damit sich niemand zweimal dieselbe Zahl holt —
    /// der Gang fragt nach jedem Lager, und eine stehengebliebene Zahl waere
    /// beim naechsten Mal doppelt gezaehlt.
    /// </summary>
    public (int Fetched, int Stowed) TakeTotals()
    {
        var totals = (_sinceFetched, _sinceStowed);
        _sinceFetched = 0;
        _sinceStowed = 0;
        return totals;
    }

    // ---------- Einstieg ----------

    /// <summary>Holt fehlende Koeder aus dem Lager in den Beutel.</summary>
    public void Start(Restock restock, Stash stash) => Begin(restock, stash, false);

    /// <summary>Legt Koeder weg, die kein Fisch der Liste mehr braucht.</summary>
    public void StartStow(Restock restock, Stash stash) => Begin(restock, stash, true);

    private void Begin(Restock restock, Stash stash, bool stowing)
    {
        _queue.Clear();
        _moved = 0;
        _shifted = 0;
        _failures = 0;
        _lastName = string.Empty;
        _restock = restock;
        _stash = stash;
        _stowing = stowing;
        Running = true;

        if (stash.Ready)
        {
            if (stowing)
                FillStow(restock);
            else
                Fill(restock.Rows);

            return;
        }

        if (!stash.CanOpenItself)
        {
            Stop($"No {stash.Name.ToLowerInvariant()} window is open.");
            return;
        }

        stash.Open();
        _openUntil = Environment.TickCount64 + OpenTimeoutMs;
        Status = $"Opening the {stash.Name.ToLowerInvariant()}...";
    }

    public void Stop(string reason)
    {
        _queue.Clear();
        Running = false;
        _openUntil = 0;
        Status = reason;
    }

    // ---------- Gruende, wenn nichts geht ----------

    /// <summary>
    /// Warum gerade nichts zu holen ist, oder null, wenn es etwas gibt.
    ///
    /// Ein Knopf, der sich nur versteckt, sagt nicht, ob nichts zu tun ist oder
    /// etwas nicht geht.
    /// </summary>
    public string? Blocker(IEnumerable<Restock.Row> rows, Stash stash)
    {
        if (!stash.Ready)
        {
            // Die Satteltasche macht der Knopf selbst auf, dann genuegt der
            // gemerkte Inhalt zur Beurteilung. Zum Gehilfen muss der Spieler.
            if (!stash.CanOpenItself)
                return "No retainer is open — talk to one at a summoning bell first.";

            return rows.Any(r => !r.Ignored && r.Target > r.Bag && stash.Remembered(r) > 0)
                ? null
                : $"Nothing remembered {stash.Into} is bait your bags are short of.";
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
            return "The inventory is not readable right now.";

        foreach (var row in rows)
        {
            if (row.Ignored || row.Target - row.Bag <= 0)
                continue;

            foreach (var type in stash.Containers)
            {
                var bag = inv->GetInventoryContainer(type);
                if (bag == null || !bag->IsLoaded)
                    continue;

                for (ushort i = 0; i < bag->Size; i++)
                {
                    var slot = bag->GetInventorySlot(i);
                    if (slot != null && slot->ItemId == row.BaitId)
                        return null;
                }
            }
        }

        return $"Nothing {stash.Into} is bait your bags are short of.";
    }

    /// <summary>Warum gerade nichts wegzuraeumen ist, oder null.</summary>
    public string? StowBlocker(Restock restock, Stash stash)
    {
        if (!stash.Ready && !stash.CanOpenItself)
            return "No retainer is open — talk to one at a summoning bell first.";

        var inv = InventoryManager.Instance();
        if (inv == null)
            return "The inventory is not readable right now.";

        var needed = NeededIds(restock);
        var kept = 0;
        var left = new Dictionary<uint, int>();

        foreach (var type in Bags)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (ushort i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->ItemId;
                if (!Tackle.Contains(itemId) || needed.Contains(itemId))
                    continue;

                var keep = config.Targets.GetValueOrDefault(itemId, 0);
                if (!left.TryGetValue(itemId, out var inBag))
                    left[itemId] = inBag = Restock.CountInInventory(itemId);

                if (inBag - (int)slot->Quantity >= keep)
                    return null;

                kept++;
            }
        }

        return kept > 0
            ? $"{kept} stack(s) are no longer needed, but stowing a whole one would leave you " +
              "under the target you set — the game moves whole stacks only."
            : "Every bait in your bags is still needed by a fish on your list.";
    }

    /// <summary>
    /// Die Koeder, die ein Fisch der Liste noch braucht.
    ///
    /// Aus der Liste selbst, nicht aus der angezeigten Tabelle: Wer die
    /// vollstaendige Anzeige ausgeschaltet hat, sieht ausgemusterte Koeder gar
    /// nicht — sie liegen aber trotzdem im Beutel.
    /// </summary>
    private HashSet<uint> NeededIds(Restock restock)
    {
        var needed = new HashSet<uint>();
        foreach (var row in restock.Rows)
            if (row.Needed)
                needed.Add(row.BaitId);

        // Der Ozeanvorrat zaehlt als gebraucht, obwohl ihn kein Fisch der Liste
        // anfordert. Er steht hier und nicht als eigene Ausnahme weiter unten,
        // damit Knopf und Ausfuehrung dieselbe Antwort geben: Beide fragen
        // diese eine Stelle.
        if (config.KeepOceanBait)
            foreach (var id in OceanFishing.Baits)
                needed.Add(id);

        return needed;
    }

    // ---------- Zusammenstellen ----------

    private int Fill(IEnumerable<Restock.Row> rows)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;

        // Was zu kurz ist. Ignorierte und volle Koeder stehen gar nicht erst
        // drin, damit die Regel sie nicht noch einmal pruefen muss.
        var shortBy = new Dictionary<uint, int>();
        foreach (var row in rows)
        {
            var room = row.Target - row.Bag;
            if (!row.Ignored && room > 0)
                shortBy[row.BaitId] = room;
        }

        var names = new Dictionary<uint, string>();
        foreach (var row in rows)
            names.TryAdd(row.BaitId, row.Name);

        var (offers, places) = Survey(_stash.Containers);
        foreach (var index in StashPlan.Fetch(offers, shortBy, FreeSlots(false)))
        {
            var offer = offers[index];
            var (container, slot) = places[index];
            _queue.Add(new Move(offer.ItemId, names.GetValueOrDefault(offer.ItemId, "bait"),
                container, slot, offer.Amount, false));
        }

        Running = _queue.Count > 0;
        Status = Running ? $"{_queue.Count} stack(s) to fetch." : "Nothing to fetch — your bags are full.";
        _openUntil = 0;

        Plugin.Log.Information($"[MasterBaiter] {_stash.Name}: {_queue.Count} stack(s) to fetch.");

        return _queue.Count;
    }

    /// <summary>
    /// Zaehlt auf, was in diesen Faechern liegt.
    ///
    /// Die einzige Stelle, die das Spiel fragt — was danach damit geschieht,
    /// entscheidet <see cref="StashPlan"/> ohne Spielzugriff.
    /// </summary>
    private (List<StashPlan.Offer> Offers, List<(InventoryType Container, ushort Slot)> Places)
        Survey(InventoryType[] containers)
    {
        var offers = new List<StashPlan.Offer>();
        var places = new List<(InventoryType, ushort)>();

        var inv = InventoryManager.Instance();
        if (inv == null)
            return (offers, places);

        foreach (var type in containers)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (ushort i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                offers.Add(new StashPlan.Offer(slot->ItemId, (int)slot->Quantity));
                places.Add((type, i));
            }
        }

        return (offers, places);
    }

    /// <summary>
    /// Stellt zusammen, was weggeraeumt werden kann.
    ///
    /// Weggeraeumt wird nur, was <b>kein Fisch der Liste mehr braucht</b>. Ein
    /// Ueberschuss ueber die Zielmenge ist kein Grund: Wer dreihundert als Ziel
    /// hat und vierhundert besitzt, will die hundert im Beutel behalten.
    ///
    /// Und nur ganze Stapel, die nicht unter die Zielmenge fuehren.
    /// </summary>
    /// <summary>
    /// Stellt zusammen, was weggeraeumt werden kann.
    ///
    /// Die Regel steht in <see cref="StashPlan.Stow"/>; hier wird nur
    /// aufgezaehlt, was im Beutel liegt, und hinterher wieder auf Faecher
    /// abgebildet.
    /// </summary>
    private int FillStow(Restock restock)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;

        var needed = NeededIds(restock);
        var (all, places) = Survey(Bags);

        // Nur Angelzeug kommt in Frage. Alles andere im Beutel geht das Plugin
        // nichts an.
        var offers = new List<StashPlan.Offer>();
        var where = new List<(InventoryType Container, ushort Slot)>();
        for (var i = 0; i < all.Count; i++)
        {
            if (!Tackle.Contains(all[i].ItemId))
                continue;

            offers.Add(all[i]);
            where.Add(places[i]);
        }

        var inBag = new Dictionary<uint, int>();
        foreach (var offer in offers)
            inBag.TryAdd(offer.ItemId, Restock.CountInInventory(offer.ItemId));

        // Nur eine ausdruecklich gesetzte Zielmenge zaehlt hier. Die
        // Voreinstellung gilt fuer Koeder, die man braucht — bei einem
        // ausgemusterten waere sie ein Grund, Ballast zu behalten.
        var keep = new Dictionary<uint, int>();
        foreach (var offer in offers)
            keep.TryAdd(offer.ItemId, config.Targets.GetValueOrDefault(offer.ItemId, 0));

        var take = StashPlan.Stow(offers, needed, keep, inBag, FreeSlots(true), out var keptWhole);

        foreach (var index in take)
        {
            var offer = offers[index];
            var (container, slot) = where[index];
            _queue.Add(new Move(offer.ItemId, Restock.ItemName(offer.ItemId),
                container, slot, offer.Amount, true));
        }

        Running = _queue.Count > 0;
        Status = Running
            ? $"{_queue.Count} stack(s) to stow."
            : $"Nothing to stow — the {_stash.Name.ToLowerInvariant()} is full.";
        _openUntil = 0;

        Plugin.Log.Information(
            $"[MasterBaiter] {_stash.Name}: {_queue.Count} stack(s) of bait no fish needs to stow" +
            (keptWhole > 0
                ? $", {keptWhole} kept because stowing them would leave you under the target."
                : "."));

        return _queue.Count;
    }


    // ---------- Ausfuehren ----------

    public void Tick()
    {
        if (!Running)
            return;

        var now = Environment.TickCount64;

        // Warten, bis die Faecher nach dem Oeffnen da sind.
        if (_openUntil > 0)
        {
            if (_stash.Ready)
            {
                if (_stowing)
                {
                    if (_restock is { } pending)
                        FillStow(pending);
                }
                else
                    Fill(_restock?.Rows ?? []);

                return;
            }

            if (now <= _openUntil)
                return;

            Fail($"The {_stash.Name.ToLowerInvariant()} did not open, so nothing was moved.");
            return;
        }

        if (now < _nextAt)
            return;

        if (_queue.Count == 0)
        {
            Running = false;
            var verb = _stowing ? "Stowed" : "Fetched";
            var where = _stowing ? _stash.Into : _stash.OutOf;

            Status = _moved == 1
                ? $"{verb} {_shifted} {_lastName} {where}."
                : $"{verb} {_shifted} items in {_moved} stacks {where}.";

            Plugin.Log.Information($"[MasterBaiter] {_stash.Name}: {Status}");

            // Der Chat erfaehrt nur das Ergebnis, nicht jeden Stapel: Je
            // Bewegung eine Zeile waere Gespamme, und der Chat gehoert dem
            // Spieler.
            if (_moved > 0)
                ChatReport.Say(config, Status);

            return;
        }

        // Zwischendurch kann das Fenster zugegangen sein.
        if (!_stash.Ready)
        {
            Fail($"The {_stash.Name.ToLowerInvariant()} closed, so nothing more was moved.");
            return;
        }

        var move = _queue[0];
        _queue.RemoveAt(0);
        _nextAt = now + DelayMs;

        if (FreeSlot(move.IntoStash) is not var (bag, slot))
        {
            Fail(move.IntoStash
                ? $"The {_stash.Name.ToLowerInvariant()} is full, so nothing more was stowed."
                : "No free inventory slot, so nothing more was fetched.");
            return;
        }

        var inv = InventoryManager.Instance();
        if (inv == null)
            return;

        // Gemessen wird am Beutel: Beim Holen muss er steigen, beim Wegraeumen
        // fallen. Auf den Rueckgabewert allein waere kein Verlass — ein Zug, der
        // angenommen und nicht ausgefuehrt wird, sieht genauso aus.
        var before = Restock.CountInInventory(move.BaitId);
        var result = inv->MoveItemSlot(move.From, move.Slot, bag, slot, true);
        var after = Restock.CountInInventory(move.BaitId);
        var changed = move.IntoStash ? before - after : after - before;

        if (changed > 0)
        {
            _moved++;
            _failures = 0;
            _shifted += changed;

            if (move.IntoStash)
                _sinceStowed += changed;
            else
                _sinceFetched += changed;
            _lastName = move.Name;
            Status = $"{move.Name}: {changed} {(move.IntoStash ? "stowed" : "fetched")}.";
            Plugin.Log.Information(
                $"[MasterBaiter] {_stash.Name}: {move.Name} {(move.IntoStash ? "-" : "+")}{changed}.");
            return;
        }

        _failures++;
        Plugin.Log.Warning(
            $"[MasterBaiter] {_stash.Name}: {move.Name} did not move (result {result}), skipped.");

        if (_failures < MaxFailures)
            return;

        Fail(move.IntoStash
            ? $"The game refused {MaxFailures} moves in a row — the {_stash.Name.ToLowerInvariant()} " +
              "is probably full."
            : $"The game refused {MaxFailures} moves in a row, so nothing more was tried.");
    }

    private void Fail(string reason)
    {
        Stop(reason);
        Plugin.Log.Warning($"[MasterBaiter] {_stash.Name}: {reason}");
        ChatReport.Warn(config, reason);
    }

    // ---------- Faecher ----------

    /// <summary>Wie viele Faecher auf der Zielseite frei sind.</summary>
    private int FreeSlots(bool inStash)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;

        var free = 0;

        foreach (var type in inStash ? _stash.Containers : Bags)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (ushort i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>Ein freies Fach auf der Zielseite, oder null.</summary>
    private (InventoryType Bag, ushort Slot)? FreeSlot(bool inStash)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return null;

        foreach (var type in inStash ? _stash.Containers : Bags)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (ushort i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0)
                    return (type, i);
            }
        }

        return null;
    }
}
