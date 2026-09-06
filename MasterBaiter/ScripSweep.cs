namespace MasterBaiter;

/// <summary>
/// Geht den Scrip-Tausch Reiter fuer Reiter durch und kauft auf jedem Blatt,
/// was fehlt.
///
/// Der Grund fuer den Umweg: Das Fenster zeigt immer nur eine Unterkategorie.
/// Koeder verteilen sich aber ueber mehrere ("Lv. 50 Materials/Bait",
/// "Lv. 90 Bait/Tokens" …). Ein Kauflauf auf dem gerade offenen Blatt findet
/// deshalb nur einen Bruchteil und meldet den Rest als "nicht im Fenster".
///
/// Umgeschaltet wird ueber <see cref="ScripShop"/>, gekauft ueber die normale
/// <see cref="PurchaseQueue"/>. Der Durchlauf wartet jede Seite ab, bevor er
/// weiterblaettert — zwei Kaufschlangen gleichzeitig gehen schief.
/// </summary>
internal sealed class ScripSweep(Restock restock, PurchaseQueue queue, VendorIndex vendors)
{
    private enum Step { OpenCategory, OpenSubCategory, BuyPage }

    /// <summary>Das Fenster braucht ein paar Frames, bis der neue Reiter in den AtkValues steht.</summary>
    private const int SettleMs = 520;

    private Step _step;
    private int _category;
    private int _tab;
    private long _nextAt;
    private bool _harvest;
    private int _pages;
    private int _bought;
    private int _skipped;
    private int _skippedCategories;

    public bool Running { get; private set; }
    public string Status { get; private set; } = string.Empty;

    public void Start()
    {
        if (!ScripShop.Available)
        {
            Status = "No scrip exchange open.";
            return;
        }

        _step = Step.OpenCategory;
        _category = 0;
        _tab = 0;
        _nextAt = 0;
        _harvest = false;
        _pages = 0;
        _bought = 0;
        _skipped = 0;
        _skippedCategories = 0;
        Running = true;
        Status = "Scanning the scrip exchange…";

        Plugin.Log.Information(
            $"[MasterBaiter] Scrip sweep started, {ScripShop.CategoryCount} categories, " +
            $"{vendors.ScripCategoryCount} of them known to carry bait.");
    }

    public void Stop(string reason)
    {
        if (!Running)
            return;
        Running = false;
        queue.Stop(reason);
        Status = reason;
        Plugin.Log.Information($"[MasterBaiter] Scrip sweep stopped: {reason}");
    }

    public void Tick()
    {
        if (!Running)
            return;

        // Solange die Seite abgekauft wird, nicht weiterblaettern.
        if (queue.Running)
            return;

        if (!ScripShop.Available || !ShopWindowReader.IsOpen)
        {
            Finish("Scrip exchange closed.");
            return;
        }

        var now = Environment.TickCount64;
        if (now < _nextAt)
            return;

        if (_harvest)
        {
            _bought += queue.Bought;
            _skipped += queue.Skipped;
            _harvest = false;
        }

        switch (_step)
        {
            case Step.OpenCategory:
                if (_category >= ScripShop.CategoryCount)
                {
                    Finish(null);
                    return;
                }

                if (Missing() == 0)
                {
                    Finish("Nothing left to buy.");
                    return;
                }

                // Der Tausch fuehrt sechs Oberkategorien, Koeder liegen aber nur
                // in einer davon. Die anderen anzuspringen kostet je Reiter eine
                // knappe Sekunde Wartezeit und bringt garantiert nichts.
                var name = ScripShop.CategoryName(_category);
                if (!vendors.ScripCategoryOfInterest(name))
                {
                    Plugin.Log.Debug($"[MasterBaiter] Skipping {name}, no bait in it.");
                    _skippedCategories++;
                    _category++;
                    return;
                }

                if (!ScripShop.SelectCategory(_category))
                {
                    // Kategorie nicht waehlbar, etwa weil der Beruf fehlt.
                    _category++;
                    return;
                }

                _tab = 0;
                _step = Step.OpenSubCategory;
                _nextAt = Pacing.Next(SettleMs);
                return;

            case Step.OpenSubCategory:
                if (_tab >= ScripShop.SubCategoryCount)
                {
                    _category++;
                    _step = Step.OpenCategory;
                    return;
                }

                if (!ScripShop.SelectSubCategory(_tab))
                {
                    _tab++;
                    return;
                }

                _step = Step.BuyPage;
                _nextAt = Pacing.Next(SettleMs);
                return;

            case Step.BuyPage:
                BuyCurrentPage();
                _tab++;
                _step = Step.OpenSubCategory;
                _nextAt = Pacing.Next(SettleMs);
                return;
        }
    }

    private void BuyCurrentPage()
    {
        restock.AnnotateShop();
        _pages++;

        var here = restock.Rows.Count(r => !r.Ignored && r.InShop && r.Missing > 0);
        var page = $"{ScripShop.CategoryName(_category)} / {ScripShop.SubCategoryName(_category, _tab)}";

        if (here == 0)
        {
            Plugin.Log.Debug($"[MasterBaiter] {page}: nothing missing here.");
            return;
        }

        Plugin.Log.Information($"[MasterBaiter] {page}: {here} bait(s) to buy.");
        queue.Start(restock.Rows);
        _harvest = true;
        Status = $"{page} — {here} bait(s)";
    }

    private int Missing()
        => restock.Rows.Count(r => !r.Ignored && r.Missing > 0);

    private void Finish(string? reason)
    {
        Running = false;
        Status = $"Scrip sweep done: {_bought} purchases across {_pages} tabs"
                 + (_skippedCategories > 0 ? $", {_skippedCategories} categories skipped" : string.Empty)
                 + (_skipped > 0 ? $", {_skipped} baits skipped." : ".");
        Plugin.Log.Information($"[MasterBaiter] {Status}" + (reason != null ? $" ({reason})" : string.Empty));
        restock.Refresh();
    }
}
