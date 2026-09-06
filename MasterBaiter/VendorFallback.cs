using System.Numerics;
using System.Reflection;
using Newtonsoft.Json;

namespace MasterBaiter;

/// <summary>
/// Verkaufsstellen aus der Eorzea-Datenbank, erzeugt von
/// tools/fetch-vendor-locations.js und als Ressource eingebettet.
///
/// Wird nur gebraucht, weil das Level-Sheet des Spiels viele neuere NPCs nicht
/// kennt — die werden ueber die Kartendateien platziert. Die Datenbank liefert
/// allerdings nur Kartenkoordinaten ohne Hoehe. Fuer die Anzeige genuegt das;
/// zum Hinlaufen wird daraus in <see cref="Resolve"/> eine Weltkoordinate
/// gerechnet, deren Hoehe spaeter vnavmesh auf dem Navigationsnetz sucht.
/// </summary>
internal sealed class VendorFallback
{
    private sealed class Entry
    {
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("vendors")] public List<Row> Vendors { get; set; } = [];
    }

    private sealed class Row
    {
        [JsonProperty("npc")] public string Npc { get; set; } = string.Empty;
        [JsonProperty("area")] public string Area { get; set; } = string.Empty;
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
    }

    private readonly Dictionary<uint, List<Row>> _raw = new();
    private readonly Dictionary<uint, List<VendorIndex.Vendor>> _byBait = new();

    public int BaitCount => _raw.Count;

    public VendorFallback()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MasterBaiter.vendor-locations.json");
        if (stream == null)
        {
            Plugin.Log.Warning("[MasterBaiter] vendor-locations.json is missing, no fallback locations.");
            return;
        }

        using var reader = new StreamReader(stream);
        var parsed = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(reader.ReadToEnd());
        if (parsed == null)
            return;

        foreach (var (key, value) in parsed)
            if (uint.TryParse(key, out var id) && value.Vendors.Count > 0)
                _raw[id] = value.Vendors;
    }

    /// <summary>
    /// Rechnet die Kartenkoordinaten in Weltkoordinaten um und sucht den
    /// naechstgelegenen Aetheryten. Braucht die Spieldaten, laeuft deshalb erst
    /// beim Aufbau des Index.
    /// </summary>
    public void Resolve(
        Func<string, (uint Territory, short OffsetX, short OffsetY, ushort SizeFactor)?> findZone,
        Func<uint, Vector3, (uint Id, string Name, uint ShardId)> nearestAetheryte)
    {
        _byBait.Clear();
        var zones = new Dictionary<string, (uint Territory, short OffsetX, short OffsetY, ushort SizeFactor)?>();

        foreach (var (baitId, rows) in _raw)
        {
            var list = new List<VendorIndex.Vendor>();
            foreach (var r in rows)
            {
                if (!zones.TryGetValue(r.Area, out var zone))
                    zones[r.Area] = zone = findZone(r.Area);

                var world = Vector3.Zero;
                var territory = 0u;
                var aetheryte = (Id: 0u, Name: string.Empty, ShardId: 0u);

                if (zone is { } z)
                {
                    territory = z.Territory;
                    world = new Vector3(
                        VendorIndex.ToWorldCoordinate(r.X, z.OffsetX, z.SizeFactor),
                        0f, // Hoehe unbekannt, vnavmesh sucht sie auf dem Netz
                        VendorIndex.ToWorldCoordinate(r.Y, z.OffsetY, z.SizeFactor));
                    aetheryte = nearestAetheryte(territory, world);
                }

                // Die mitgelieferte Tabelle stammt von den Gil-Kraemern der
                // Eorzea-Datenbank; ohne die Einstufung landeten sie in der
                // Rangfolge hinter dem Scrip-Tausch.
                list.Add(new VendorIndex.Vendor(
                    r.Npc, r.Area, r.X, r.Y, world, territory,
                    aetheryte.Id, aetheryte.Name, ApproximateHeight: true, DataId: 0,
                    AethernetId: aetheryte.ShardId, Kind: VendorIndex.VendorKind.GilShop));
            }

            _byBait[baitId] = list;
        }
    }

    public IReadOnlyList<VendorIndex.Vendor> For(uint baitId)
        => _byBait.TryGetValue(baitId, out var v) ? v : [];
}
