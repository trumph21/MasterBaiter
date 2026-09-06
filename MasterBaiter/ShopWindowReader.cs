using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Liest das offene Haendlerfenster aus und loest Kaeufe aus. Zwei Bauarten:
///
///   Shop                  Gil-Kraemer
///   ShopExchangeCurrency  Waehrungslaeden
///   InclusionShop         Scrip-Tausch ("Item Exchange")
///
/// Beide kaufen ueber denselben Rueckruf (0, Index, Menge), die Felder liegen
/// aber woanders. Beim Waehrungsladen ist der Index zudem nicht die Zeilennummer,
/// sondern steht in einem eigenen Feld — wer die Schleifenvariable nimmt, kauft
/// den falschen Artikel.
///
/// Feldpositionen aus ECommons (AddonMaster.Shop, AddonMaster.ShopExchangeCurrency).
/// </summary>
internal static unsafe class ShopWindowReader
{
    // Gil-Kraemer
    private const int GilCountIndex = 2;
    private const int GilItemIdBase = 441;
    private const int GilPriceBase = 75;

    // Waehrungsladen
    private const int CurrencyCountIndex = 4;
    private const int CurrencyItemIdBase = 1066;
    private const int CurrencyPriceBase = 456;
    private const int CurrencyIndexBase = 1310;

    // Scrip-Tausch. Andere Schrittweite und ein anderer Rueckrufcode.
    //
    // Die Feldfolge je Eintrag steht in AddonInclusionShop.InclusionShopAtkValues:
    //   +0 unbenutzt  +1 ItemId  +2 IconId  +3 Stapelgroesse  +4 Bestand
    //   +5 GiveCount  +6..8 GiveItemId  +9..11 GiveIconId  +12..14 GiveAmount
    //   +15 MaxAmount  +16 Flags  +17 Index
    //
    // "Give" ist aus Sicht des Spielers: gegeben werden Scrips, das ist der Preis.
    // Feld +17 ist der Index, den der Rueckruf erwartet. Er ist NICHT die
    // Zeilennummer — sobald eine Unterkategorie gefiltert ist, laufen beide
    // auseinander und die Schleifenvariable kauft den falschen Posten.
    private const int InclusionCountIndex = 298;
    private const int InclusionItemIdBase = 300;
    private const int InclusionOwnedBase = 303;
    private const int InclusionCurrencyBase = 305;
    private const int InclusionCostBase = 311;
    private const int InclusionMaxBase = 314;
    private const int InclusionIndexBase = 316;
    private const int InclusionStride = 18;
    private const int InclusionOpcode = 14;

    /// <summary>CurrencyId 0 heisst Gil. MaxAmount 0 heisst "keine Angabe".</summary>
    public readonly record struct ShopEntry(int Index, uint ItemId, uint Price, uint CurrencyId = 0, uint MaxAmount = 0);

    private static AtkUnitBase* Get(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name);
        return ptr.IsNull || !ptr.IsVisible ? null : (AtkUnitBase*)ptr.Address;
    }

    private static AtkUnitBase* GilShop() => Get("Shop");
    private static AtkUnitBase* CurrencyShop() => Get("ShopExchangeCurrency");
    private static AtkUnitBase* InclusionShop() => Get("InclusionShop");

    /// <summary>Das offene Fenster, egal welcher Bauart.</summary>
    public static AtkUnitBase* GetShopAddon()
    {
        var addon = GilShop();
        if (addon != null)
            return addon;
        addon = CurrencyShop();
        return addon != null ? addon : InclusionShop();
    }

    public static bool IsOpen => GetShopAddon() != null;

    /// <summary>Welche Bauart gerade offen ist, fuer Meldungen.</summary>
    public static string Kind => GilShop() != null ? "gil shop"
        : CurrencyShop() != null ? "currency shop"
        : InclusionShop() != null ? "scrip exchange"
        : "none";

    public static List<ShopEntry> ReadEntries()
    {
        var result = new List<ShopEntry>();

        var gil = GilShop();
        if (gil != null)
        {
            var values = gil->AtkValuesSpan;
            if (values.Length <= GilCountIndex)
                return result;

            var count = (int)values[GilCountIndex].UInt;
            for (var i = 0; i < count && GilItemIdBase + i < values.Length; i++)
            {
                var itemId = values[GilItemIdBase + i].UInt;
                if (itemId == 0)
                    continue;
                var price = GilPriceBase + i < values.Length ? values[GilPriceBase + i].UInt : 0;
                result.Add(new ShopEntry(i, itemId, price));
            }

            return result;
        }

        var inclusion = InclusionShop();
        if (inclusion != null)
        {
            var iv = inclusion->AtkValuesSpan;
            if (iv.Length <= InclusionCountIndex)
                return result;

            var rows = (int)iv[InclusionCountIndex].UInt;
            for (var i = 0; i < rows; i++)
            {
                var itemAt = InclusionItemIdBase + i * InclusionStride;
                var costAt = InclusionCostBase + i * InclusionStride;
                var currencyAt = InclusionCurrencyBase + i * InclusionStride;
                var maxAt = InclusionMaxBase + i * InclusionStride;
                var indexAt = InclusionIndexBase + i * InclusionStride;
                if (itemAt >= iv.Length)
                    break;

                var id = iv[itemAt].UInt;
                if (id == 0)
                    continue;

                // Den mitgelieferten Index nehmen, nicht die Zeilennummer.
                var index = indexAt < iv.Length ? (int)iv[indexAt].UInt : i;

                result.Add(new ShopEntry(index, id,
                    costAt < iv.Length ? iv[costAt].UInt : 0,
                    currencyAt < iv.Length ? iv[currencyAt].UInt : 0,
                    maxAt < iv.Length ? iv[maxAt].UInt : 0));
            }

            // Sicherung gegen eine falsch gedeutete Feldposition: Der Kaufindex
            // muss je Posten verschieden sein. Waeren alle gleich, kaeuften wir
            // wieder und wieder denselben Artikel und zahlten dafuer Scrips.
            // In dem Fall lieber auf die Zeilennummer zurueckfallen.
            var indices = new HashSet<int>();
            var clash = result.Any(e => !indices.Add(e.Index));
            if (clash)
            {
                Plugin.Log.Warning("[MasterBaiter] The scrip exchange returned duplicate purchase indices; " +
                                   "falling back to row numbers.");
                for (var i = 0; i < result.Count; i++)
                    result[i] = result[i] with { Index = i };
            }

            return result;
        }

        var currency = CurrencyShop();
        if (currency == null)
            return result;

        var cv = currency->AtkValuesSpan;
        if (cv.Length <= CurrencyCountIndex)
            return result;

        var entries = (int)cv[CurrencyCountIndex].UInt;
        for (var i = 0; i < entries && CurrencyItemIdBase + i < cv.Length; i++)
        {
            var itemId = cv[CurrencyItemIdBase + i].UInt;
            if (itemId == 0)
                continue;

            var price = CurrencyPriceBase + i < cv.Length ? cv[CurrencyPriceBase + i].UInt : 0;
            // Der Rueckrufindex steht separat und weicht von der Zeilennummer ab.
            var index = CurrencyIndexBase + i < cv.Length ? (int)cv[CurrencyIndexBase + i].UInt : i;
            result.Add(new ShopEntry(index, itemId, price));
        }

        return result;
    }

    /// <summary>Schreibt den gesamten Inhalt des Haendlerfensters ins Log.</summary>
    public static void DumpToLog()
    {
        var addon = GetShopAddon();
        if (addon == null)
        {
            Plugin.Log.Information("[MasterBaiter] Dump: no shop window open.");
            return;
        }

        var entries = ReadEntries();
        var where = ScripShop.Available
            ? $", {ScripShop.CategoryName(ScripShop.SelectedCategory)} / " +
              $"{ScripShop.SubCategoryName(ScripShop.SelectedCategory, ScripShop.SelectedSubCategory)} " +
              $"(category {ScripShop.SelectedCategory + 1}/{ScripShop.CategoryCount}, " +
              $"tab {ScripShop.SelectedSubCategory + 1}/{ScripShop.SubCategoryCount})"
            : string.Empty;
        Plugin.Log.Information($"[MasterBaiter] Dump: {Kind}{where}, {entries.Count} entries with an item id.");

        foreach (var e in entries)
            Plugin.Log.Information($"[MasterBaiter]   index {e.Index,4}  item {e.ItemId,6}  {e.Price,6} " +
                                   $"{(e.CurrencyId != 0 ? Restock.ItemName(e.CurrencyId) : "gil"),-28}  {Restock.ItemName(e.ItemId)}");
    }

    /// <summary>Loest den Kauf von <paramref name="amount"/> Stueck des Eintrags aus.</summary>
    public static bool Buy(int index, int amount)
    {
        var addon = GetShopAddon();
        if (addon == null || amount <= 0)
            return false;

        // Der Scrip-Tausch erwartet einen eigenen Rueckrufcode.
        var opcode = InclusionShop() != null ? InclusionOpcode : 0;
        AddonCallback.Fire(addon, true, opcode, index, amount);
        return true;
    }
}
