using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace MasterBaiter;

/// <summary>
/// Wo die Rufglocken stehen.
///
/// Gebaut wie <see cref="MarketBoards"/>, und aus demselben Grund: Das
/// Level-Sheet des Spiels kennt diese Objekte nicht, ihre Standorte muessen
/// also von woanders kommen. Hier lernt das Plugin sie beim Vorbeigehen und
/// merkt sie sich im Spielstand.
///
/// Anders als bei den Brettern gibt es keine mitgelieferte Tabelle: Die
/// Koordinaten haette ich raten muessen, und eine geratene Position schickt den
/// Charakter an eine Wand. Eine Glocke, an der man einmal war, ist eine
/// Auskunft; eine erfundene ist keine.
///
/// Die fuenf Objekt-Ids stammen aus dem EObjName-Blatt. Sie stehen praktisch
/// immer neben einem Marktbrett — 2000401 zu 2000402, 2000441 zu 2000442.
/// </summary>
internal static class SummoningBells
{
    /// <summary>Wie oft nach einer unbekannten Glocke gesucht wird.</summary>
    private const int ScanIntervalMs = 3000;

    private static long _nextScan;

    /// <summary>Die Objekt-Ids, an denen eine Rufglocke zu erkennen ist.</summary>
    private static readonly HashSet<uint> ObjectIds =
    [
        2000072, 2000401, 2000403, 2000439, 2000441,
    ];

    public static bool IsBell(uint dataId) => dataId != 0 && ObjectIds.Contains(dataId);

    public static int KnownCount(Configuration config) => config.LearnedBells.Count;

    /// <summary>
    /// Sieht nach, ob im aktuellen Gebiet eine noch unbekannte Glocke steht.
    /// Zeitlich gedrosselt, deshalb guenstig genug fuer jeden Frame.
    /// </summary>
    public static void Learn(Configuration config)
    {
        var now = Environment.TickCount64;
        if (now < _nextScan)
            return;
        _nextScan = now + ScanIntervalMs;

        var territory = Plugin.ClientState.TerritoryType;
        if (territory == 0)
            return;

        if (config.LearnedBells.Any(b => b.Territory == territory))
            return;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !ObjectIds.Contains(obj.BaseId))
                continue;

            config.LearnedBells.Add(new Configuration.Spot
            {
                Name = "Summoning bell",
                Territory = territory,
                DataId = obj.BaseId,
                X = obj.Position.X,
                Y = obj.Position.Y,
                Z = obj.Position.Z,
            });
            config.Save();

            Plugin.Log.Information(
                $"[MasterBaiter] Learned a summoning bell in territory {territory} " +
                $"at ({obj.Position.X:0.0}, {obj.Position.Y:0.0}, {obj.Position.Z:0.0}).");
            return;
        }
    }

    /// <summary>Alle bekannten Glocken als anfahrbare Ziele.</summary>
    public static List<VendorIndex.Vendor> All(Configuration config, VendorIndex vendors)
    {
        var result = new List<VendorIndex.Vendor>();

        foreach (var b in config.LearnedBells)
            result.Add(vendors.MakeSpot("Summoning bell", b.Territory, new Vector3(b.X, b.Y, b.Z), b.DataId));

        return result;
    }

    /// <summary>
    /// Die Glocke, an die gefahren wird.
    ///
    /// Ist eine feste gewaehlt und erreichbar, gewinnt sie ohne Rechnung.
    /// Sonst die naechstgelegene: Eine im aktuellen Gebiet gewinnt, dann
    /// entscheidet die Entfernung zum Aetheryten.
    /// </summary>
    public static VendorIndex.Vendor? Nearest(Configuration config, VendorIndex vendors, uint from = 0)
    {
        if (!vendors.Ready)
            return null;

        var candidates = All(config, vendors);

        // Nur wenn dort auch etwas erreichbar ist. Eine feste Wahl, die den
        // Gang scheitern laesst, waere schlechter als gar keine.
        if (config.PreferredBellTerritory != 0)
        {
            var pinned = candidates
                .Where(b => b.Territory == config.PreferredBellTerritory && Reach.CanReach(b))
                .ToList();

            if (pinned.Count > 0)
                candidates = pinned;
        }

        var here = from != 0 ? from : Plugin.ClientState.TerritoryType;
        VendorIndex.Vendor? best = null;
        var bestScore = float.MaxValue;

        foreach (var bell in candidates)
        {
            if (!Reach.CanReach(bell))
                continue;

            var distance = vendors.DistanceToAetheryte(bell);
            var score = bell.Territory == here ? distance : distance + 100000f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            best = bell;
        }

        return best;
    }
}
