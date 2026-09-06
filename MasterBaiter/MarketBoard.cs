using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MasterBaiter;

/// <summary>
/// Kauft Koeder am Marktbrett, fuer die es keinen Haendler gibt.
///
/// Der wesentliche Unterschied zum Haendler: Dort steht der Preis in den
/// Spieldaten und ist unveraenderlich. Hier setzt ihn ein anderer Spieler. Ein
/// Kauflauf ohne Obergrenze kann deshalb beliebig teuer werden — durch einen
/// Tippfehler beim Anbieter ebenso wie durch Absicht. Zwei Bremsen gelten
/// immer und lassen sich nicht abschalten, nur hoeher stellen:
///
///   MarketMaxUnitPrice   Angebote darueber werden nicht angefasst
///   MarketMaxGilPerRun   danach endet der Durchlauf
///
/// Gekauft wird immer ein ganzes Angebot; Teilmengen sieht das Marktbrett
/// nicht vor. Ein Stapel, der groesser ist als der offene Bedarf, wird deshalb
/// gar nicht erst in Betracht gezogen — auch nicht, wenn er sonst der einzige
/// waere. Bei zehn fehlenden Kunstkoedern einen Neunundneunziger zu nehmen,
/// kostet das Zehnfache fuer Ware, die niemand braucht.
///
/// Die Suche wird ueber die spieleigene Abfrage angestossen: Suchitem setzen,
/// RequestData rufen. Das ist derselbe Weg, den das Spiel selbst nimmt, kein
/// nachgebautes Paket — schlaegt er fehl, passiert nichts. Antwortet der Server
/// binnen einiger Sekunden nicht, sucht der Spieler selbst und der Durchlauf
/// kauft dann; er bleibt also in jedem Fall benutzbar.
/// </summary>
internal sealed unsafe class MarketBoard(Configuration config, Restock restock)
{
    private const int DelayMs = 450;
    private const int ResultTimeoutMs = 8000;
    private const int SearchTimeoutMs = 6000;
    private const int ListingSettleMs = 1500;
    private const uint GilItemId = 1;

    private static readonly string[] AddonNames = ["ItemSearch", "ItemSearchResult"];

    /// <summary>Was ein Koeder am Brett zuletzt gekostet hat, und wann.</summary>
    public readonly record struct Quote(uint UnitPrice, int Listings, DateTime When);

    private readonly Dictionary<uint, Quote> _quotes = new();
    private readonly List<uint> _queue = [];
    private long _nextAt;
    private long _resultDeadline;
    private bool _awaitingResult;
    private int _countBefore;
    private uint _waitingFor;
    private uint _requestedFor;
    private long _requestDeadline;
    private long _settledAt;

    public bool Running { get; private set; }
    public string Status { get; private set; } = string.Empty;

    /// <summary>Der zuletzt gesehene Preis, falls dieser Koeder schon abgefragt wurde.</summary>
    public Quote? QuoteFor(uint baitId)
        => _quotes.TryGetValue(baitId, out var q) ? q : null;
    public int Spent { get; private set; }
    public int Bought { get; private set; }

    /// <summary>Das Marktbrett-Fenster, egal ob Suche oder Ergebnisliste.</summary>
    public static bool IsOpen
    {
        get
        {
            foreach (var name in AddonNames)
            {
                var ptr = Plugin.GameGui.GetAddonByName(name);
                if (!ptr.IsNull && ptr.IsVisible)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Koeder, die hier in Frage kommen: gebraucht, aber nirgends zu kaufen.</summary>
    public static IEnumerable<Restock.Row> Candidates(IEnumerable<Restock.Row> rows, VendorIndex vendors)
        => rows.Where(r => !r.Ignored && r.Missing > 0 && vendors.For(r.BaitId).Count == 0);

    public void Start(IEnumerable<Restock.Row> rows, VendorIndex vendors)
    {
        _queue.Clear();
        Spent = 0;
        Bought = 0;
        _awaitingResult = false;
        _waitingFor = 0;
        _requestedFor = 0;
        _settledAt = 0;

        foreach (var r in Candidates(rows, vendors))
            _queue.Add(r.BaitId);

        Running = _queue.Count > 0;
        Status = Running ? $"{_queue.Count} baits queued." : "Nothing to buy here.";

        if (Running)
            Plugin.Log.Information(
                $"[MasterBaiter] Market board run: {_queue.Count} baits, " +
                $"at most {config.MarketMaxUnitPrice} gil each and {config.MarketMaxGilPerRun} gil in total.");
    }

    public void Stop(string reason)
    {
        _queue.Clear();
        Running = false;
        _awaitingResult = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!Running)
            return;

        var now = Environment.TickCount64;

        if (YesNoDialog.IsOpen)
        {
            if (now < _nextAt)
                return;
            YesNoDialog.Confirm();
            _nextAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        if (!IsOpen)
        {
            Stop("Stopped: market board closed.");
            return;
        }

        if (now < _nextAt)
            return;

        if (_queue.Count == 0)
        {
            Finish("Done.");
            return;
        }

        var baitId = _queue[0];
        var target = config.TargetFor(baitId);
        var have = Restock.CountInInventory(baitId);

        if (_awaitingResult)
        {
            WaitForResult(baitId, target, have, now);
            return;
        }

        if (have >= target)
        {
            _queue.RemoveAt(0);
            _waitingFor = 0;
            _requestedFor = 0;
            _settledAt = 0;
            return;
        }

        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null)
        {
            Stop("Stopped: no market board data.");
            return;
        }

        // Ob die Liste zu diesem Koeder gehoert, laesst sich NICHT an
        // SearchItemId ablesen — das Feld setzen wir gleich selbst, es waere
        // per Konstruktion wahr. Es zaehlt nur, ob Angebote fuer dieses Item
        // vorliegen.
        //
        // WaitingForListings taugt dafuer ebenfalls nicht: Wird die Abfrage am
        // Ergebnisfenster vorbei angestossen, bleibt das Flag gesetzt, obwohl
        // die Angebote laengst da sind. Wer darauf wartet, wartet ewig.
        var listings = proxy->Listings;
        var count = Math.Min((int)proxy->ListingCount, listings.Length);
        var ready = count > 0 && listings[0].ItemId == baitId;

        if (!ready)
        {
            // Einmal die spieleigene Abfrage anstossen: Suchitem setzen,
            // RequestData rufen. Derselbe Weg, den das Spiel selbst nimmt.
            if (_requestedFor != baitId)
            {
                _requestedFor = baitId;
                _requestDeadline = now + SearchTimeoutMs;
                proxy->SearchItemId = baitId;
                var accepted = proxy->RequestData();
                Plugin.Log.Information(
                    $"[MasterBaiter] Asked the market board for {Restock.ItemName(baitId)} " +
                    $"(request {(accepted ? "accepted" : "refused")}).");
                _nextAt = Pacing.NextWithPause(DelayMs);
                return;
            }

            if (now < _requestDeadline)
            {
                _nextAt = Pacing.NextWithPause(DelayMs);
                return;
            }

            // Frist abgelaufen. Der Spieler sucht selbst; sobald Angebote da
            // sind, laeuft der Kauf weiter. Die Zahlen stehen dabei, sonst
            // laesst sich spaeter nicht sagen, woran es lag.
            if (_waitingFor != baitId)
            {
                _waitingFor = baitId;
                Status = $"Search for {Restock.ItemName(baitId)} — waiting.";
                Plugin.Log.Information(
                    $"[MasterBaiter] No listings for {Restock.ItemName(baitId)} yet " +
                    $"(pending={proxy->WaitingForListings}, count={proxy->ListingCount}, " +
                    $"first={(count > 0 ? listings[0].ItemId : 0)}). " +
                    "Search for it yourself and the purchase will follow.");
            }

            _nextAt = Pacing.NextWithPause(DelayMs);
            _settledAt = 0;
            return;
        }

        // Die Angebote kommen seitenweise. Kurz nachfassen lassen, sonst wird
        // aus den ersten zwei gewaehlt, obwohl gleich ein guenstigeres folgt.
        if (_settledAt == 0)
        {
            _settledAt = now + ListingSettleMs;
            _nextAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        if (now < _settledAt)
        {
            _nextAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        // Den gesehenen Preis merken, auch wenn gleich nichts gekauft wird.
        // Genau dann ist er naemlich interessant: Er sagt, wie weit die
        // Preisgrenze danebenliegt.
        _quotes[baitId] = new Quote(Cheapest(proxy, baitId), count, DateTime.Now);

        // Was noch ausgegeben werden darf. Beides zaehlt: die Obergrenze fuer
        // den Durchlauf und das Gil, das tatsaechlich da ist.
        var gil = Restock.CountInInventory(GilItemId);
        var budget = Math.Min(config.MarketMaxGilPerRun - Spent, gil);

        var pick = BestListing(proxy, target - have, budget, out var unitPrice, out var quantity);
        if (pick < 0)
        {
            // Drei Gruende, warum nichts passt, und sie fuehren zu ganz
            // verschiedenen Entscheidungen: zu teuer je Stueck, Stapel zu gross
            // fuer das Budget, oder schlicht kein Angebot.
            var cheapest = Cheapest(proxy, baitId);
            // Drei Ausgaenge, drei verschiedene Abhilfen: Preisgrenze anheben,
            // Zielmenge anheben, oder Budget anheben. Sie zu verwechseln
            // schickt den Spieler an die falsche Stellschraube.
            var smallest = SmallestStack(proxy, baitId, (uint)config.MarketMaxUnitPrice);
            var reason = cheapest == 0
                ? "No listings at all."
                : cheapest > (uint)config.MarketMaxUnitPrice
                    ? $"Cheapest listing is {cheapest} gil each, above the {config.MarketMaxUnitPrice} gil limit."
                    : smallest == 0 || smallest > target - have
                        ? $"Cheapest listing is {cheapest} gil each, but the smallest stack on offer is " +
                          $"{smallest} and only {target - have} are missing."
                        : $"Cheapest listing is {cheapest} gil each, but no stack of {target - have} " +
                          $"or fewer fits the remaining {budget} gil.";

            Plugin.Log.Warning($"[MasterBaiter] {Restock.ItemName(baitId)}: skipped. {reason}");
            _queue.RemoveAt(0);
            _waitingFor = 0;
            _requestedFor = 0;
            _settledAt = 0;
            return;
        }

        var cost = (int)(unitPrice * quantity);

        fixed (MarketBoardListing* listing = &listings[pick])
        {
            proxy->SetLastPurchasedItem(listing);
            proxy->SendPurchaseRequestPacket();
        }

        Spent += cost;
        _countBefore = have;
        _awaitingResult = true;
        _resultDeadline = now + ResultTimeoutMs;
        _nextAt = Pacing.NextWithPause(DelayMs);
        Status = $"{Restock.ItemName(baitId)}: {quantity} for {cost} gil";
        Plugin.Log.Information($"[MasterBaiter] Buying {quantity}x {Restock.ItemName(baitId)} " +
                               $"at {unitPrice} gil each ({cost} gil).");
    }

    private void WaitForResult(uint baitId, int target, int have, long now)
    {
        if (have > _countBefore)
        {
            _awaitingResult = false;
            // Das gekaufte Angebot ist weg; die Liste muss neu geholt werden,
            // sonst wird gleich wieder darauf gezeigt.
            _requestedFor = 0;
            _settledAt = 0;
            Bought++;
            Plugin.Log.Information(
                $"[MasterBaiter] {Restock.ItemName(baitId)}: +{have - _countBefore}, now {have}/{target}. " +
                $"{Spent} gil spent so far.");
            _nextAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        if (now < _resultDeadline)
            return;

        _awaitingResult = false;
        Plugin.Log.Warning($"[MasterBaiter] {Restock.ItemName(baitId)}: the purchase was not registered, skipped.");
        _queue.RemoveAt(0);
        _waitingFor = 0;
        _requestedFor = 0;
        _settledAt = 0;
    }

    /// <summary>
    /// Das guenstigste bezahlbare Angebot. Bei gleichem Stueckpreis gewinnt das
    /// kleinere, damit der Bedarf nicht weit ueberschritten wird.
    /// </summary>
    private int BestListing(InfoProxyItemSearch* proxy, int needed, int budget,
        out uint unitPrice, out uint quantity)
    {
        unitPrice = 0;
        quantity = 0;
        var best = -1;

        var listings = proxy->Listings;
        var count = Math.Min((int)proxy->ListingCount, listings.Length);

        for (var i = 0; i < count; i++)
        {
            ref var listing = ref listings[i];
            if (listing.ItemId == 0 || listing.Quantity == 0)
                continue;
            if (listing.UnitPrice > (uint)config.MarketMaxUnitPrice)
                continue;

            // Gekauft wird immer der ganze Stapel. Passt er nicht ins Budget,
            // faellt nur dieses Angebot weg — nicht der ganze Durchlauf.
            if ((long)listing.UnitPrice * listing.Quantity > budget)
                continue;

            // Nie mehr kaufen als fehlt. Ein Stapel ist unteilbar, also ist ein
            // zu grosser schlicht kein Angebot — auch nicht als letzte
            // Moeglichkeit. Sonst kostet ein Ziel von zehn Stueck den Preis von
            // neunundneunzig.
            if (listing.Quantity > needed)
                continue;

            // Unter den passenden der guenstigste je Stueck; bei gleichem Preis
            // der groessere, der bringt den Bestand in einem Kauf weiter.
            var better = best < 0
                         || listing.UnitPrice < unitPrice
                         || (listing.UnitPrice == unitPrice && listing.Quantity > quantity);
            if (!better)
                continue;

            best = i;
            unitPrice = listing.UnitPrice;
            quantity = listing.Quantity;
        }

        return best;
    }

    /// <summary>Der kleinste angebotene Stapel innerhalb der Preisgrenze.</summary>
    private static uint SmallestStack(InfoProxyItemSearch* proxy, uint itemId, uint maxUnitPrice)
    {
        uint smallest = 0;
        var listings = proxy->Listings;
        var count = Math.Min((int)proxy->ListingCount, listings.Length);

        for (var i = 0; i < count; i++)
        {
            ref var listing = ref listings[i];
            if (listing.ItemId != itemId || listing.Quantity == 0)
                continue;
            if (listing.UnitPrice > maxUnitPrice)
                continue;
            if (smallest == 0 || listing.Quantity < smallest)
                smallest = listing.Quantity;
        }

        return smallest;
    }

    /// <summary>Der guenstigste Stueckpreis, ohne Ruecksicht auf die Grenze.</summary>
    private static uint Cheapest(InfoProxyItemSearch* proxy, uint itemId)
    {
        uint cheapest = 0;
        var listings = proxy->Listings;
        var count = Math.Min((int)proxy->ListingCount, listings.Length);

        for (var i = 0; i < count; i++)
        {
            ref var listing = ref listings[i];
            if (listing.ItemId != itemId || listing.Quantity == 0)
                continue;
            if (cheapest == 0 || listing.UnitPrice < cheapest)
                cheapest = listing.UnitPrice;
        }

        return cheapest;
    }

    private void Finish(string reason)
    {
        Running = false;
        _queue.Clear();
        Status = $"Market board: {Bought} purchases, {Spent} gil. {reason}";
        Plugin.Log.Information($"[MasterBaiter] {Status}");
        restock.Refresh();
    }
}
