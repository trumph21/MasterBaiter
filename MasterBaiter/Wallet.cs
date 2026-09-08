using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>
/// Was der Charakter an Zahlungsmitteln bei sich hat.
///
/// Die Preise liegen als Text vor ("5 Purple Gatherers' Scrip"), der Bestand
/// aber haengt an einer Item-Kennung. Diese Klasse schlaegt die eine in die
/// andere um und zaehlt nach.
///
/// Gesucht wird nur unter Waehrungen: Kategorie 100 ("Currency") und die
/// Cosmic-Exploration-Gutschriften, die das Spiel unter 63 ("Other") fuehrt.
/// Ohne diese Einschraenkung koennte ein Waehrungsname zufaellig auf ein
/// gleichnamiges Handelsgut treffen.
/// </summary>
internal static class Wallet
{
    private const uint CurrencyCategory = 100;

    private static Dictionary<string, uint>? _ids;

    /// <summary>
    /// Gesetzt beim Laden. Ohne Spielstand wird nichts gemerkt — dann zaehlt
    /// nur, was gerade lesbar ist.
    /// </summary>
    internal static Configuration? Store;

    /// <summary>
    /// Waehrungen, die einmal vorgekommen sind, werden weiter beobachtet.
    ///
    /// Sonst haengt das Gedaechtnis daran, was die Routenplanung gerade
    /// zufaellig fragt: Eine Waehrung, deren Koeder nicht fehlen, wird nie
    /// abgefragt — und ist dann auch nicht gemerkt, wenn man das Gebiet
    /// verlaesst, in dem sie als einziges lesbar war.
    /// </summary>
    private static readonly HashSet<uint> Watched = [];

    private const int RefreshIntervalMs = 5000;
    private static long _nextRefresh;

    /// <summary>
    /// Liest alle beobachteten Waehrungen nach. Gehoert in den Framework-Takt.
    /// </summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshIntervalMs;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return;

        // Was frueher schon gemerkt wurde, bleibt beobachtet — auch ueber einen
        // Neustart hinweg.
        if (Store is { } store)
            foreach (var id in store.Currencies.Keys)
                Watched.Add(id);

        foreach (var id in Watched)
        {
            var live = Restock.CountInInventory(id);
            if (live > 0)
                Remember(id, live);
        }
    }

    /// <summary>Die Kennung zu einem Waehrungsnamen, oder null.</summary>
    public static uint? IdOf(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        // Gil steht in keiner Ladentabelle als Kaufgegenstand, ist aber die
        // haeufigste Waehrung ueberhaupt.
        if (currency.Equals("gil", StringComparison.OrdinalIgnoreCase))
            return 1;

        _ids ??= Build();
        if (!_ids.TryGetValue(currency.Trim(), out var id))
            return null;

        Watched.Add(id);
        return id;
    }

    /// <summary>
    /// Wie viel davon der Charakter hat. Unbekannte Waehrung heisst null.
    ///
    /// Eine gelesene Null wird nicht geglaubt, wenn frueher schon einmal mehr
    /// zu sehen war: Sie heisst bei ortsgebundenen Waehrungen "hier nicht
    /// lesbar", nicht "keine da". Umgekehrt wird jede Zahl ueber null sofort
    /// uebernommen — sie ist die frischere Wahrheit.
    /// </summary>
    public static int? Balance(string currency)
    {
        var id = IdOf(currency);
        if (id == null)
            return null;

        var live = Restock.CountInInventory(id.Value);
        if (live > 0)
        {
            Remember(id.Value, live);
            return live;
        }

        // Nie etwas davon gesehen heisst nicht null, sondern unbekannt — und
        // wer daraus null macht, sperrt jeden Haendler aus, der diese Waehrung
        // nimmt. Genau das ist mit den Cosmocredits passiert: Ausserhalb der
        // Cosmic Exploration lesen sie sich als null, und die Route liess jeden
        // Halt dort stumm weg.
        //
        // Behoben war damals nur die Haelfte: Ein einmal gemerkter Bestand
        // ueberlebt die Null, ein nie gesehener wurde weiter zu einer. Wer das
        // Plugin frisch aufsetzt und nie mit offener Cosmic Exploration
        // eingeloggt war, hatte den alten Fehler unveraendert.
        //
        // Die Route rechnet Unbekanntes als unbegrenzt und faehrt hin; ist dort
        // wirklich nichts, sagt es der Laden laut. Ein falsches "nein" hier
        // waere still.
        return Remembered(id.Value);
    }

    /// <summary>Wann dieser Bestand zuletzt gesehen wurde.</summary>
    public static DateTime? SeenAt(uint id) =>
        Store?.CurrenciesSeen.TryGetValue(id, out var when) == true ? when : null;

    /// <summary>Der gemerkte Bestand, falls es einen gibt.</summary>
    public static int? Remembered(uint id) =>
        Store?.Currencies.TryGetValue(id, out var amount) == true ? amount : null;

    /// <summary>Haelt fest, was gerade zu sehen war.</summary>
    public static void Remember(uint id, int amount)
    {
        if (Store is not { } store)
            return;

        if (store.Currencies.TryGetValue(id, out var known) && known == amount)
            return;

        store.Currencies[id] = amount;
        store.CurrenciesSeen[id] = DateTime.Now;
        store.Save();
    }

    /// <summary>
    /// Bucht ab, was gerade ausgegeben wurde.
    ///
    /// Der Kauf ist der einzige Augenblick, in dem sicher ist, dass sich der
    /// Bestand geaendert hat — und bei einer Waehrung, die man anderswo nicht
    /// lesen kann, die einzige Gelegenheit, das Gedaechtnis nachzufuehren.
    /// </summary>
    public static void Spend(uint id, int amount)
    {
        if (amount <= 0 || Remembered(id) is not { } known)
            return;

        Remember(id, Math.Max(0, known - amount));
    }

    private static Dictionary<string, uint> Build()
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        var items = Plugin.DataManager.GetExcelSheet<Item>();
        if (items == null)
            return map;

        foreach (var item in items)
        {
            var category = item.ItemUICategory.RowId;
            var name = item.Name.ExtractText().Trim();

            if (name.Length == 0)
                continue;

            var isCurrency = category == CurrencyCategory
                             || name.Contains("cosmocredit", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("lunar credit", StringComparison.OrdinalIgnoreCase);

            if (isCurrency)
                map.TryAdd(name, item.RowId);
        }

        Plugin.Log.Information($"[MasterBaiter] {map.Count} currencies known.");
        return map;
    }
}
