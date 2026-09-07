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
///
/// Auch der zweite Weg endet an einem Aetheryten: Der Fahrzeug-NPC steht in
/// Mare Lamentorum, und dorthin kommt nur, wer Bestways Burrow freigeschaltet
/// hat. Das wurde bisher ungeprueft durchgewunken — wer den Punkt nicht hat,
/// bekam trotzdem "Go", fuhr los und blieb stehen.
/// </summary>
internal static class Reach
{
    /// <summary>
    /// Wird beim Laden einmal gesetzt. Ohne Zuweisung faellt die Pruefung des
    /// Cosmic-Wegs weg, statt ihn faelschlich zu sperren.
    /// </summary>
    internal static CosmicTravel? Cosmic;

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
            return CanRideToOrbit(out reason);

        if (vendor.AetheryteId == 0)
        {
            reason = "no teleport point in that zone";
            return false;
        }

        return Teleportable.Check(vendor.AetheryteId, out reason);
    }

    public static bool CanReach(VendorIndex.Vendor vendor) => CanReach(vendor, out _);

    /// <summary>Steht der Weg zu den Planeten diesem Charakter offen?</summary>
    private static bool CanRideToOrbit(out string reason)
    {
        reason = string.Empty;

        if (Cosmic is not { } cosmic)
            return true;

        // Wer schon dort steht, braucht den Teleport nicht.
        if (Plugin.ClientState.TerritoryType == cosmic.GateTerritory)
            return true;

        var gate = cosmic.GateAetheryteId;
        if (gate == 0)
            return true;

        if (Teleportable.Check(gate, out var why))
            return true;

        reason = $"the ride starts at {cosmic.GateName}, and that zone is out of reach ({why})";
        return false;
    }

    /// <summary>Faehrt man dorthin ueber die Cosmic Exploration statt per Teleport?</summary>
    public static bool IsCosmic(VendorIndex.Vendor vendor) => CosmicTravel.PlanetOf(vendor.Zone) != null;
}
