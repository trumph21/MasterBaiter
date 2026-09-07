using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>
/// Trennt Kunstkoeder von echten Koedern.
///
/// Das Spiel fuehrt beide unter derselben Kategorie ("Fishing Tackle", 33), der
/// Unterschied fuer den Spieler ist aber betraechtlich: Kunstkoeder kosten das
/// Zehn- bis Tausendfache und werden nicht verbraucht wie Wuermer. 300 Stueck
/// davon zu kaufen ergibt keinen Sinn.
///
/// Zwei Merkmale zeigen darauf, und beide werden benutzt:
///
///   Feld 4 == 40000   trennt in den Spieldaten sauber, ist aber unbenannt
///   Beschreibung      nennt "lure" oder "jig" im Klartext
///
/// Ueber alle 188 Angelkoeder stimmen sie in 185 Faellen ueberein; die drei
/// Abweichungen (Spinnerbait, Streamer, Metal Spinner) sind Kunstkoeder, deren
/// Beschreibung das Wort nur nicht verwendet. Das unbenannte Feld ist also das
/// genauere Merkmal — aber weil es unbenannt ist, kann es bei einem Patch eine
/// andere Bedeutung bekommen. Deshalb zaehlt beides, und die beiden Zahlen
/// stehen im Log: laufen sie auseinander, faellt es auf.
/// </summary>
internal static class Tackle
{
    private const uint FishingTackleCategory = 33;
    private const uint LureMarker = 40000;

    private static readonly HashSet<uint> Lures = [];
    private static readonly List<uint> All = [];

    /// <summary>
    /// Dieselben Kennungen zum Nachschlagen. Die Liste haelt die Reihenfolge,
    /// aber ein <c>Contains</c> darueber laeuft je Aufruf durch alle 188 —
    /// und die Frage "ist das Angelzeug?" wird je Inventarfach gestellt.
    /// </summary>
    private static readonly HashSet<uint> Lookup = [];
    private static bool _built;

    /// <summary>Alle Angelkoeder des Spiels, Kunstkoeder eingeschlossen.</summary>
    public static IReadOnlyList<uint> AllIds
    {
        get
        {
            Build();
            return All;
        }
    }

    public static int LureCount => Lures.Count;
    public static int MarkerCount { get; private set; }
    public static int DescriptionCount { get; private set; }

    /// <summary>Ist das ueberhaupt Angelzeug?</summary>
    public static bool Contains(uint itemId)
    {
        Build();
        return Lookup.Contains(itemId);
    }

    public static bool IsLure(uint itemId)
    {
        Build();
        return Lures.Contains(itemId);
    }

    public static void Build()
    {
        if (_built)
            return;
        _built = true;

        // Die Beschreibung wird gegen das englische Blatt geprueft, damit die
        // Einstufung nicht von der Spielsprache abhaengt.
        var english = Plugin.DataManager.GetExcelSheet<Item>(Dalamud.Game.ClientLanguage.English);
        if (english == null)
        {
            Plugin.Log.Warning("[MasterBaiter] Could not read the item sheet; every bait counts as bait.");
            return;
        }

        foreach (var item in english)
        {
            if (item.ItemUICategory.RowId != FishingTackleCategory)
                continue;

            var byMarker = item.Unknown4 == LureMarker;
            var description = item.Description.ExtractText();
            var byText = description.Contains("lure", StringComparison.OrdinalIgnoreCase)
                         || description.Contains("jig", StringComparison.OrdinalIgnoreCase);

            All.Add(item.RowId);
            Lookup.Add(item.RowId);

            if (byMarker) MarkerCount++;
            if (byText) DescriptionCount++;
            if (byMarker || byText)
                Lures.Add(item.RowId);
        }

        Plugin.Log.Information(
            $"[MasterBaiter] {All.Count} fishing tackle items, {Lures.Count} of them lures " +
            $"({MarkerCount} by marker, {DescriptionCount} by description).");
    }
}
