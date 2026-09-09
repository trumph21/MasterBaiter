using System.Reflection;
using System.Text.Json;
using MasterBaiter;
using Xunit;

namespace MasterBaiter.Tests;

/// <summary>
/// Die drei Ozeankoeder sind drei Zahlen im Quelltext, und eine falsche Zahl
/// waere hier besonders unangenehm: Sie schuetzte einen fremden Gegenstand vor
/// dem Wegraeumen und liesse den echten Koeder gehen — beides lautlos, beides
/// erst zwei Minuten vor der Faehre zu bemerken.
///
/// Deshalb werden sie gegen die mitgelieferte Haendlertabelle geprueft, die
/// dieselben Gegenstaende unter Namen fuehrt. Das ist keine zweite Quelle im
/// strengen Sinn — beide stammen aus derselben Datenbank —, aber es faengt den
/// Tippfehler, und der ist die wahrscheinliche Ursache.
/// </summary>
public class OceanFishingTests
{
    private static readonly Dictionary<uint, string> Shipped = LoadShippedNames();

    private static Dictionary<uint, string> LoadShippedNames()
    {
        // Dieselbe Ressource, die das Plugin liest.
        using var stream = typeof(OceanFishing).Assembly
            .GetManifestResourceStream("MasterBaiter.vendor-locations.json");
        Assert.NotNull(stream);

        using var document = JsonDocument.Parse(stream!);
        var names = new Dictionary<uint, string>();
        foreach (var entry in document.RootElement.EnumerateObject())
            if (uint.TryParse(entry.Name, out var id)
                && entry.Value.TryGetProperty("name", out var name))
                names[id] = name.GetString() ?? string.Empty;

        return names;
    }

    [Theory]
    [InlineData(OceanFishing.Ragworm, "Ragworm")]
    [InlineData(OceanFishing.Krill, "Krill")]
    [InlineData(OceanFishing.PlumpWorm, "Plump Worm")]
    public void TheIdIsTheBaitItClaimsToBe(uint id, string name)
    {
        Assert.True(Shipped.ContainsKey(id), $"Item {id} is not in the shipped vendor table at all.");
        Assert.Equal(name, Shipped[id]);
    }

    [Fact]
    public void AllThreeAreProtectedAndNothingElseIs()
    {
        Assert.Equal(3, OceanFishing.Baits.Length);
        Assert.All(OceanFishing.Baits, id => Assert.True(OceanFishing.Contains(id)));

        // Der Nachbar im Nummernkreis ist Versatile Lure und gehoert nicht dazu.
        // Ein Zahlendreher landet genau dort.
        Assert.False(OceanFishing.Contains(29717));
        Assert.False(OceanFishing.Contains(29713));
    }

    [Fact]
    public void ProtectedBaitIsNeverStowed()
    {
        // Die Regel, wie StashPlan sie sieht: Was in "needed" steht, bleibt
        // liegen — auch ohne Zielmenge und ohne einen Fisch, der es anfordert.
        var offers = OceanFishing.Baits.Select(id => new StashPlan.Offer(id, 500)).ToList();
        var needed = new HashSet<uint>(OceanFishing.Baits);
        var inBag = OceanFishing.Baits.ToDictionary(id => id, _ => 500);

        Assert.Empty(StashPlan.Stow(offers, needed, new Dictionary<uint, int>(), inBag, 30, out var kept));
        Assert.Equal(0, kept);
    }
}
