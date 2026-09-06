namespace MasterBaiter;

/// <summary>
/// Kommt man zu diesem Haendler?
///
/// Die Frage wurde bisher an drei Stellen verschieden beantwortet — im
/// Go-Knopf, in der Routenplanung und in der Selbstpruefung —, und nach dem
/// Einbau der Cosmic Exploration lagen zwei davon falsch: Sie fragten nur den
/// Teleportpunkt ab und schlossen damit ausgerechnet die Planeten aus, zu denen
/// das Plugin inzwischen faehrt.
///
/// Drei Wege fuehren zum Ziel, und nur alle drei zusammen ergeben die richtige
/// Antwort:
///
///   1. Man steht schon dort.
///   2. Das Gebiet ist ein Planet der Cosmic Exploration — dorthin geht es
///      ueber den Fahrzeug-NPC, nicht ueber einen Aetheryten.
///   3. Der Aetheryt ist freigeschaltet.
/// </summary>
internal static class Reach
{
    public static bool CanReach(VendorIndex.Vendor vendor, out string reason)
    {
        reason = string.Empty;

        // Ohne Standort geht gar nichts — mit einem aber noch lange nicht
        // zwingend ein Teleport.
        if (!vendor.HasPosition)
        {
            reason = "no usable coordinates";
            return false;
        }

        if (Plugin.ClientState.TerritoryType == vendor.Territory)
            return true;

        if (CosmicTravel.PlanetOf(vendor.Zone) != null)
            return true;

        if (vendor.AetheryteId == 0)
        {
            reason = "no teleport point in that zone";
            return false;
        }

        return Teleportable.Check(vendor.AetheryteId, out reason);
    }

    public static bool CanReach(VendorIndex.Vendor vendor) => CanReach(vendor, out _);

    /// <summary>Faehrt man dorthin ueber die Cosmic Exploration statt per Teleport?</summary>
    public static bool IsCosmic(VendorIndex.Vendor vendor) => CosmicTravel.PlanetOf(vendor.Zone) != null;
}
