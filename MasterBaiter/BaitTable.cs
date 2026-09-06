using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace MasterBaiter;

/// <summary>
/// Zuordnung Fisch-Item-Id -> Startkoeder, erzeugt von tools/extract-baits.js
/// aus den Fischdaten von GatherBuddy Reborn und als Ressource eingebettet.
/// </summary>
internal sealed class BaitTable
{
    private sealed class Entry
    {
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("bait")] public uint Bait { get; set; }
        [JsonProperty("chain")] public uint[] Chain { get; set; } = [];
    }

    private readonly Dictionary<uint, Entry> _fish = new();

    public int FishCount => _fish.Count;

    public BaitTable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MasterBaiter.fish-baits.json");
        if (stream == null)
        {
            Plugin.Log.Error("fish-baits.json is missing from the embedded resources.");
            return;
        }

        using var reader = new StreamReader(stream);
        var raw = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(reader.ReadToEnd());
        if (raw == null)
            return;

        foreach (var (key, value) in raw)
            if (uint.TryParse(key, out var id))
                _fish[id] = value;
    }

    /// <summary>Alle Koeder, die in der Tabelle vorkommen.</summary>
    public IReadOnlyCollection<uint> AllBaitIds => _fish.Values.Select(e => (uint)e.Bait).Distinct().ToList();

    public bool IsFish(uint itemId) => _fish.ContainsKey(itemId);

    /// <summary>Startkoeder eines Fisches, oder null wenn das Item kein bekannter Fisch ist.</summary>
    public uint? BaitFor(uint itemId) => _fish.TryGetValue(itemId, out var e) ? e.Bait : null;

    public string FishName(uint itemId) => _fish.TryGetValue(itemId, out var e) ? e.Name : string.Empty;

    /// <summary>Alle Koeder, die fuer die uebergebenen Items gebraucht werden, mit den zugehoerigen Fischen.</summary>
    public Dictionary<uint, List<uint>> BaitsFor(IEnumerable<uint> itemIds)
    {
        var result = new Dictionary<uint, List<uint>>();
        foreach (var id in itemIds)
        {
            if (!_fish.TryGetValue(id, out var e))
                continue;
            if (!result.TryGetValue(e.Bait, out var list))
                result[e.Bait] = list = [];
            list.Add(id);
        }
        return result;
    }
}
