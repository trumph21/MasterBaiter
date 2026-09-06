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
        [JsonProperty("amount")] public int Amount { get; set; }
        [JsonProperty("currency")] public string Currency { get; set; } = string.Empty;
    }

    private readonly Dictionary<uint, string> _prices = new();

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

        foreach (var (key, value) in parsed)
        {
            if (!uint.TryParse(key, out var id))
                continue;
            if (value.Amount <= 0 || string.IsNullOrWhiteSpace(value.Currency))
                continue;

            // Dieselbe Schreibweise wie die Spieldaten, damit PriceTag beides
            // liest und die Vorschau nicht zwei Formate kennen muss.
            _prices[id] = $"{value.Amount} {value.Currency}";
        }

        Plugin.Log.Information($"[MasterBaiter] {_prices.Count} fallback prices loaded.");
    }

    public string? For(uint baitId) => _prices.GetValueOrDefault(baitId);
}
