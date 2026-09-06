using System.Text;

namespace MasterBaiter;

/// <summary>
/// Geht jeden Koeder der Liste durch und schreibt auf, was fehlt.
///
/// Hintergrund: Die Bezugsquellen kamen ueber mehrere Anlaeufe zusammen, und
/// jedes Mal fiel eine Luecke erst auf, weil ein einzelner Koeder falsch
/// angezeigt wurde — fehlende Level-Eintraege, weggefilterte Aethernet-Kristalle,
/// die Aetheryten-Verknuepfung in der Gegenrichtung. Diese Pruefung sammelt alle
/// verbliebenen Luecken auf einmal, statt sie einzeln zu entdecken.
/// </summary>
internal static class SelfCheck
{
    public static void Run(Restock restock, VendorIndex vendors)
    {
        var rows = restock.Rows;
        if (rows.Count == 0)
        {
            Plugin.Log.Warning("[MasterBaiter] Self-check: no baits in the list.");
            return;
        }

        var withNavigable = 0;      // erreichbar, Go-Knopf erscheint
        var vendorButNoRoute = new List<string>();   // Haendler bekannt, aber kein Weg
        var craftedOnly = new List<string>();        // nur herstellbar
        var specialOnly = new List<string>();        // nur Sonderladen ohne Standort
        var noSource = new List<string>();           // gar nichts
        var zonesWithoutTeleport = new SortedSet<string>();
        var zonesNotAttuned = new SortedSet<string>();
        var zonesApproximate = new SortedSet<string>();

        foreach (var row in rows)
        {
            var list = vendors.For(row.BaitId);
            var navigable = 0;

            foreach (var v in list)
            {
                if (v.Navigable && !Teleportable.Check(v.AetheryteId, out _))
                    zonesNotAttuned.Add($"{v.Zone} (via {v.AetheryteName})");
                else if (v.Navigable)
                    navigable++;
                else if (v.Territory != 0 && v.AetheryteId == 0)
                    zonesWithoutTeleport.Add(v.Zone.Length > 0 ? v.Zone : $"territory {v.Territory}");
                else if (v.Territory == 0)
                    zonesWithoutTeleport.Add($"{v.Zone} (zone not resolved)");

                if (v.Navigable && v.ApproximateHeight)
                    zonesApproximate.Add(v.Zone);
            }

            if (navigable > 0)
            {
                withNavigable++;
                continue;
            }

            if (list.Count > 0)
                vendorButNoRoute.Add(row.Name);
            else if (vendors.RecipeFor(row.BaitId) != null)
                craftedOnly.Add(row.Name);
            else if (vendors.OtherSourcesFor(row.BaitId).Count > 0)
                specialOnly.Add(row.Name);
            else
                noSource.Add(row.Name);
        }

        var report = new StringBuilder();
        report.AppendLine($"[MasterBaiter] Self-check over {rows.Count} baits, {Teleportable.Count} teleport destinations unlocked:");
        report.AppendLine($"  reachable by travel : {withNavigable}");
        report.AppendLine($"  craftable only      : {craftedOnly.Count}");
        report.AppendLine($"  special shop only   : {specialOnly.Count}");
        report.AppendLine($"  vendor but no route : {vendorButNoRoute.Count}");
        report.AppendLine($"  no source at all    : {noSource.Count}");

        void Detail(string label, List<string> items)
        {
            if (items.Count == 0)
                return;
            report.AppendLine($"  {label}: {string.Join(", ", items.Take(30))}"
                              + (items.Count > 30 ? $" (+{items.Count - 30})" : string.Empty));
        }

        // Die beiden hier sind echte Luecken, die anderen sind erwartete Faelle.
        Detail("VENDOR BUT NO ROUTE", vendorButNoRoute);
        Detail("NO SOURCE", noSource);
        Detail("craftable", craftedOnly);
        Detail("special shop", specialOnly);

        if (zonesWithoutTeleport.Count > 0)
            report.AppendLine($"  ZONES WITHOUT A TELEPORT POINT: {string.Join(", ", zonesWithoutTeleport)}");
        if (zonesNotAttuned.Count > 0)
            report.AppendLine($"  ZONES YOU CANNOT TELEPORT TO: {string.Join(", ", zonesNotAttuned)}");
        if (zonesApproximate.Count > 0)
            report.AppendLine($"  zones with estimated height: {string.Join(", ", zonesApproximate)}");

        Plugin.Log.Information(report.ToString().TrimEnd());
    }
}
