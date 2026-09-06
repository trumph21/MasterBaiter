using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace MasterBaiter;

/// <summary>
/// Wo Marktbretter stehen.
///
/// Anders als Haendler stehen sie nicht im Level-Blatt — dort ergibt die Suche
/// null Treffer, obwohl die Objekt-Ids existieren. Platziert werden sie in den
/// Kartendateien (LGB), die ein Plugin zur Laufzeit nicht bezahlbar durchsucht.
///
/// Deshalb zwei Quellen:
///
///   1. Eine eingebaute Tabelle, offline aus planevent/planlive/planmap der
///      Stadtgebiete ausgelesen. Echte Koordinaten, keine geschaetzten.
///   2. Was der Spieler selbst sieht: Steht er in einem Gebiet, dessen Brett
///      noch unbekannt ist, wird die Position aus der Objektliste uebernommen
///      und gespeichert.
///
/// Die zweite Quelle deckt ab, was die erste nicht kennt — Tuliyollal und
/// Solution Nine platzieren ihre Bretter anders, und ein kuenftiger Patch kann
/// jederzeit weitere hinzufuegen, ohne dass die Tabelle nachgezogen werden muss.
/// </summary>
internal static class MarketBoards
{
    private readonly record struct Spot(uint Territory, uint DataId, float X, float Y, float Z);

    /// <summary>Wie oft nach einem unbekannten Brett gesucht wird.</summary>
    private const int ScanIntervalMs = 3000;

    private static long _nextScan;

    private static readonly Spot[] Known =
    [
        new(129, 2000402, -223.77f, 16.00f, 51.49f), // Limsa Lominsa Lower Decks
        new(129, 2000402, -270.88f, 16.00f, 51.75f), // Limsa Lominsa Lower Decks
        new(129, 2000402, -118.97f, 17.99f, 20.95f), // Limsa Lominsa Lower Decks
        new(129, 2000402, -166.96f, 18.00f, 28.47f), // Limsa Lominsa Lower Decks
        new(129, 2000402, -123.44f, 18.00f, 10.14f), // Limsa Lominsa Lower Decks
        new(129, 2000402, -270.92f, 15.98f, 39.90f), // Limsa Lominsa Lower Decks
        new(131, 2000442, 105.65f, 4.20f, -80.88f),  // Ul'dah - Steps of Thal
        new(131, 2000442, 125.79f, 4.10f, -95.05f),  // Ul'dah - Steps of Thal
        new(131, 2000442, 145.41f, 4.00f, -46.43f),  // Ul'dah - Steps of Thal
        new(131, 2000442, 156.31f, 4.10f, 9.36f),    // Ul'dah - Steps of Thal
        new(131, 2000442, 149.09f, 4.00f, -32.50f),  // Ul'dah - Steps of Thal
        new(133, 2000402, 159.13f, 15.59f, -100.84f), // Old Gridania
        new(133, 2000402, 159.37f, 15.59f, -91.17f),  // Old Gridania
        new(133, 2000402, 150.93f, 15.78f, -56.45f),  // Old Gridania
        new(133, 2000402, 162.05f, 15.78f, -52.27f),  // Old Gridania
        new(133, 2000402, 162.11f, 15.78f, -139.61f), // Old Gridania
        new(133, 2000402, 150.89f, 15.78f, -135.66f), // Old Gridania
        new(419, 2000402, -152.81f, -12.53f, -41.98f), // The Pillars
        new(628, 2000442, 2.50f, 4.00f, 47.96f),      // Kugane
        new(819, 2010285, -49.76f, -7.49f, 132.16f),  // The Crystarium
        new(819, 2010285, -53.58f, -7.67f, 140.61f),  // The Crystarium
        new(962, 2000402, 19.77f, 2.38f, -35.58f),    // Old Sharlayan
    ];

    /// <summary>Die Objekt-Ids, an denen ein Marktbrett zu erkennen ist.</summary>
    private static readonly HashSet<uint> ObjectIds = [.. Known.Select(k => k.DataId)];

    public static int BuiltInCount => Known.Length;

    /// <summary>Ist dieses Ziel ein Marktbrett und kein Haendler?</summary>
    public static bool IsBoard(uint dataId) => dataId != 0 && ObjectIds.Contains(dataId);

    /// <summary>
    /// Sieht nach, ob im aktuellen Gebiet ein noch unbekanntes Brett steht, und
    /// merkt es sich. Guenstig genug fuer jeden Frame, weil zeitlich gedrosselt.
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

        if (Known.Any(k => k.Territory == territory)
            || config.LearnedMarketBoards.Any(b => b.Territory == territory))
            return;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventObj || !ObjectIds.Contains(obj.BaseId))
                continue;

            config.LearnedMarketBoards.Add(new Configuration.Spot
            {
                Territory = territory,
                DataId = obj.BaseId,
                X = obj.Position.X,
                Y = obj.Position.Y,
                Z = obj.Position.Z,
            });
            config.Save();

            Plugin.Log.Information($"[MasterBaiter] Learned a market board in territory {territory} " +
                                   $"at ({obj.Position.X:0.0}, {obj.Position.Y:0.0}, {obj.Position.Z:0.0}).");
            return;
        }
    }

    /// <summary>Alle bekannten Bretter als anfahrbare Ziele.</summary>
    public static List<VendorIndex.Vendor> All(Configuration config, VendorIndex vendors)
    {
        var result = new List<VendorIndex.Vendor>();

        foreach (var k in Known)
            result.Add(vendors.MakeSpot("Market board", k.Territory, new Vector3(k.X, k.Y, k.Z), k.DataId));

        foreach (var b in config.LearnedMarketBoards)
            result.Add(vendors.MakeSpot("Market board", b.Territory, new Vector3(b.X, b.Y, b.Z), b.DataId));

        return result;
    }

    /// <summary>
    /// Das naechstgelegene erreichbare Brett. Eines im aktuellen Gebiet gewinnt,
    /// dann entscheidet die Entfernung zum Aetheryten — sonst laeuft man in
    /// Limsa quer durch die Stadt, obwohl eines direkt am Plaza steht.
    /// </summary>
    public static VendorIndex.Vendor? Nearest(Configuration config, VendorIndex vendors)
    {
        if (!vendors.Ready)
            return null;

        var here = Plugin.ClientState.TerritoryType;
        VendorIndex.Vendor? best = null;
        var bestScore = float.MaxValue;

        foreach (var board in All(config, vendors))
        {
            // Ueber Reach, nicht ueber den Teleportpunkt allein: Ein Brett im
            // Gebiet, in dem man gerade steht, ist erreichbar, auch wenn dorthin
            // kein Teleport fuehrt.
            if (!Reach.CanReach(board))
                continue;

            var distance = vendors.DistanceToAetheryte(board);
            var score = board.Territory == here ? distance : distance + 100000f;
            if (score >= bestScore)
                continue;

            bestScore = score;
            best = board;
        }

        return best;
    }
}
