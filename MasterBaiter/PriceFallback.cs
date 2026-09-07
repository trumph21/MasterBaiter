using System.Reflection;
using Newtonsoft.Json;

namespace MasterBaiter;

/// <summary>
/// Waehrungspreise aus der Eorzea-Datenbank, erzeugt von
/// tools/fetch-bait-prices.js und als Ressource eingebettet.
///
/// Gil-Preise stehen am Item selbst und kommen aus den Spieldaten. Was ein
/// Koeder am Scrip-Tausch kostet, findet das Plugin dort nicht — Baitbugs etwa
/// steht in keinem Gil-Laden und blieb deshalb ohne Preis, obwohl er
/// schlicht einen Purple Gatherers' Scrip kostet.
///
/// Nur Rueckfall: Was die Spieldaten hergeben, gilt. Eine mitgelieferte Datei
/// altert, wenn Square Enix einen Preis aendert; die Spieldaten nicht.
/// </summary>
internal sealed class PriceFallback
{
    private sealed class Entry
    {
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("prices")] public List<Cost> Prices { get; set; } = [];
    }

    private sealed class Cost
    {
        [JsonProperty("amount")] public int Amount { get; set; }
        [JsonProperty("currency")] public string Currency { get; set; } = string.Empty;
    }

    /// <summary>
    /// Je Koeder alle dort genannten Waehrungen, nicht nur eine.
    ///
    /// Dragonfly kostet 10 Cosmocredit bei der Cosmic Exploration und 5 Purple
    /// Gatherers' Scrip am Tausch. Beides steht auf derselben Seite; nur den
    /// ersten Eintrag zu behalten hiess, je Koeder einen zufaelligen der beiden
    /// zu verlieren.
    /// </summary>
    private readonly Dictionary<uint, List<string>> _prices = new();

    public int Count => _prices.Count;

    public PriceFallback()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MasterBaiter.bait-prices.json");
        if (stream == null)
        {
            Plugin.Log.Warning("[MasterBaiter] bait-prices.json is missing, no fallback prices.");
            return;
        }

        using var reader = new StreamReader(stream);
        var parsed = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(reader.ReadToEnd());
        if (parsed == null)
            return;

        var total = 0;
        foreach (var (key, value) in parsed)
        {
            if (!uint.TryParse(key, out var id))
                continue;

            foreach (var cost in value.Prices)
            {
                if (cost.Amount <= 0 || string.IsNullOrWhiteSpace(cost.Currency))
                    continue;

                if (!_prices.TryGetValue(id, out var list))
                    _prices[id] = list = [];

                // Dieselbe Schreibweise wie die Spieldaten, damit PriceTag
                // beides liest und die Vorschau nicht zwei Formate kennen muss.
                var text = $"{cost.Amount} {cost.Currency}";
                if (!list.Contains(text))
                {
                    list.Add(text);
                    total++;
                }
            }
        }

        Plugin.Log.Information(
            $"[MasterBaiter] {total} fallback prices for {_prices.Count} baits loaded.");
    }

    /// <summary>Alle bekannten Preise dieses Koeders.</summary>
    public IReadOnlyList<string> All(uint baitId) =>
        _prices.TryGetValue(baitId, out var list) ? list : [];
}
