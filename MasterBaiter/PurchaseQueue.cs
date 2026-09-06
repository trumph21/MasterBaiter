namespace MasterBaiter;

/// <summary>
/// Kauft nacheinander ab, statt alle Kaeufe in einem Frame abzufeuern.
///
/// Ablauf je Kaufaufruf:
///   1. Kauf ausloesen (hoechstens 99 Stueck, mehr laesst das Spiel nicht zu)
///   2. den Bestaetigungsdialog quittieren, der danach erscheint
///   3. warten, bis der Bestand im Inventar tatsaechlich steigt
///
/// Schritt 3 ist wichtig: Das Spiel verbucht den Kauf erst ein paar Frames
/// spaeter. Wer sofort nachzaehlt, sieht faelschlich einen Zugewinn von null
/// und haelt einen funktionierenden Kauf fuer gescheitert.
/// </summary>
internal sealed class PurchaseQueue(Configuration config)
{
    private sealed class Job
    {
        public uint BaitId;
        public string Name = string.Empty;
        public int Target;
        public int Attempts;
        public int Stalls;
    }

    private const int DelayMs = 320;       // Grundabstand, siehe Pacing
    private const int ResultTimeoutMs = 5000; // so lange warten wir auf die Verbuchung
    private const int MaxAttempts = 400;   // Notbremse je Koeder
    private const int MaxStalls = 3;       // so oft darf ein Kauf folgenlos bleiben
    private const int MaxPerCall = 99;     // mehr nimmt das Spiel je Kauf nicht an

    private readonly List<Job> _jobs = [];
    private long _nextActionAt;

    // Zustand eines laufenden Kaufs
    private bool _awaitingResult;
    private long _resultDeadline;
    private int _countBefore;
    private int _requested;
    private int _index;
    private int _dialogTries;

    public bool Running { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public int Remaining => _jobs.Count;
    public int Bought { get; private set; }
    public int Skipped { get; private set; }
    private int _totalItems;

    public void Start(IEnumerable<Restock.Row> rows)
    {
        _jobs.Clear();
        Bought = 0;
        Skipped = 0;
        _totalItems = 0;
        _awaitingResult = false;

        foreach (var r in rows)
        {
            if (r.Ignored || !r.InShop || r.Missing <= 0)
                continue;
            _jobs.Add(new Job { BaitId = r.BaitId, Name = r.Name, Target = r.Target });
        }

        if (_jobs.Count > 0)
        {
            Plugin.Log.Information($"[MasterBaiter] Starting run with {_jobs.Count} baits.");
        }
        else
        {
            // Ein stiller Fehlschlag ist der teuerste: Der Laden geht auf,
            // nichts passiert, und im Log steht kein Wort dazu. Deshalb hier
            // die Zahlen, an denen sich ablesen laesst, woran es lag.
            var list = rows.ToList();
            var needed = list.Count(r => !r.Ignored && r.Missing > 0);
            var inShop = list.Count(r => r.InShop);
            Plugin.Log.Information(
                $"[MasterBaiter] Nothing to buy at this {ShopWindowReader.Kind}: " +
                $"{needed} bait(s) missing, {inShop} of them found in the window." +
                (needed > 0 && inShop == 0
                    ? "  The shop window was not readable — use Dump shop to see what it holds."
                    : string.Empty));
        }

        Running = _jobs.Count > 0;
        Status = Running ? $"{_jobs.Count} baits queued." : "Nothing to buy.";
        _nextActionAt = 0;
    }

    public void Stop(string reason)
    {
        _jobs.Clear();
        Running = false;
        _awaitingResult = false;
        Status = reason;
    }

    public void Tick()
    {
        if (!Running)
            return;

        if (_jobs.Count == 0)
        {
            Running = false;
            Status = $"Done. {Bought} purchases" + (Skipped > 0 ? $", {Skipped} baits skipped." : ".");
            Plugin.Log.Information($"[MasterBaiter] Run finished: {Bought} purchases, {Skipped} skipped, " +
                                   $"{_totalItems} items total.");

            // Nur melden, wenn tatsaechlich etwas geschehen ist — ein "0 gekauft"
            // nach jedem Ladenbesuch waere Laerm.
            if (_totalItems > 0)
                ChatReport.Say(config, $"Bought {_totalItems} items" +
                                       (Skipped > 0 ? $", {Skipped} bait(s) skipped." : "."));
            return;
        }

        var now = Environment.TickCount64;

        // Bestaetigungsdialoge haben immer Vorrang, auch waehrend wir warten.
        // Der Scrip-Tausch benutzt einen eigenen statt SelectYesno.
        if (ExchangeDialog.IsOpen)
        {
            if (now < _nextActionAt)
                return;
            ExchangeDialog.Confirm(_dialogTries++);
            _nextActionAt = Pacing.Next(DelayMs * 3);
            return;
        }

        if (YesNoDialog.IsOpen)
        {
            if (now < _nextActionAt)
                return;
            YesNoDialog.Confirm();
            _nextActionAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        _dialogTries = 0;

        if (!ShopWindowReader.IsOpen)
        {
            Stop("Stopped: vendor window closed.");
            return;
        }

        var job = _jobs[0];

        if (_awaitingResult)
        {
            WaitForResult(job, now);
            return;
        }

        if (now < _nextActionAt)
            return;

        var have = Restock.CountInInventory(job.BaitId);

        if (have >= job.Target)
        {
            _jobs.RemoveAt(0);
            return;
        }

        if (job.Stalls >= MaxStalls || job.Attempts >= MaxAttempts)
        {
            Plugin.Log.Warning($"[MasterBaiter] {job.Name}: purchase has no effect ({have}/{job.Target}), skipped.");
            Skipped++;
            _jobs.RemoveAt(0);
            return;
        }

        var entry = FindEntry(job.BaitId);
        if (entry is not { } shopEntry)
        {
            Plugin.Log.Warning($"[MasterBaiter] {job.Name} is not in the shop window, skipped.");
            Skipped++;
            _jobs.RemoveAt(0);
            return;
        }

        var index = shopEntry.Index;
        var amount = Math.Min(job.Target - have, MaxPerCall);

        // Vorher nachrechnen, ob die Waehrung reicht.
        //
        // Ohne das versucht der Kauf, scheitert lautlos, und erst die Frist von
        // fuenf Sekunden verraet es — dreimal je Koeder. Bei fuenfzehn Koedern
        // ohne genug Scrips sind das Minuten, in denen nichts geschieht. Preis
        // und Waehrung stehen im Fenster; die Frage laesst sich beantworten,
        // bevor man sie stellt.
        if (shopEntry.Price > 0)
        {
            var currencyId = shopEntry.CurrencyId != 0 ? shopEntry.CurrencyId : GilItemId;
            var available = Restock.CountInInventory(currencyId);
            var affordable = available / (int)shopEntry.Price;

            if (affordable <= 0)
            {
                Plugin.Log.Warning(
                    $"[MasterBaiter] {job.Name}: not enough {Restock.ItemName(currencyId)} " +
                    $"({available} of {shopEntry.Price} needed for one), skipped.");
                Skipped++;
                _jobs.RemoveAt(0);
                return;
            }

            // So viel, wie bezahlbar ist — lieber weniger kaufen als gar nichts.
            if (affordable < amount)
            {
                Plugin.Log.Information(
                    $"[MasterBaiter] {job.Name}: {available} {Restock.ItemName(currencyId)} " +
                    $"only covers {affordable} of {amount}.");
                amount = affordable;
            }
        }
        job.Attempts++;

        _countBefore = have;
        _requested = amount;
        _index = index;

        if (!ShopWindowReader.Buy(index, amount))
        {
            job.Stalls++;
            _nextActionAt = Pacing.NextWithPause(DelayMs);
            return;
        }

        _awaitingResult = true;
        _resultDeadline = now + ResultTimeoutMs;
        _nextActionAt = Pacing.NextWithPause(DelayMs);
        Status = $"{job.Name}: {have}/{job.Target} — {_jobs.Count} baits left";
    }

    /// <summary>Wartet, bis der Bestand steigt, hoechstens aber bis zur Frist.</summary>
    private void WaitForResult(Job job, long now)
    {
        var have = Restock.CountInInventory(job.BaitId);

        if (have > _countBefore)
        {
            _awaitingResult = false;
            job.Stalls = 0;
            Bought++;
            _nextActionAt = Pacing.NextWithPause(DelayMs);
            Status = $"{job.Name}: {have}/{job.Target} — {_jobs.Count} baits left";

            var gained = have - _countBefore;
            _totalItems += gained;
            Plugin.Log.Information(
                $"[MasterBaiter] {job.Name}: +{gained} (requested {_requested}), have {have}/{job.Target}."
                + (gained != _requested ? "  WARNING: amount differs." : string.Empty));
            return;
        }

        if (now < _resultDeadline)
            return;

        _awaitingResult = false;
        job.Stalls++;
        _nextActionAt = Pacing.NextWithPause(DelayMs);
        Plugin.Log.Warning(
            $"[MasterBaiter] {job.Name}: purchase via index {_index} ({_requested} items) was not registered, " +
            $"attempt {job.Stalls} of {MaxStalls}.");
    }

    /// <summary>Gil-Item-Id, fuer Laeden ohne eigene Waehrung.</summary>
    private const uint GilItemId = 1;

    /// <summary>Der Eintrag des Koeders im offenen Fenster, oder null.</summary>
    private static ShopWindowReader.ShopEntry? FindEntry(uint itemId)
    {
        foreach (var entry in ShopWindowReader.ReadEntries())
            if (entry.ItemId == itemId)
                return entry;
        return null;
    }
}
