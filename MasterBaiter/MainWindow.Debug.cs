using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MasterBaiter;

/// <summary>
/// Der Reiter "Debug": Werkzeuge, um herauszufinden, warum etwas nicht geht.
///
/// Teil von <see cref="MainWindow"/>. Die Datei war auf zweitausend Zeilen
/// gewachsen und enthielt vier Reiter, die Vorschau und ein Dutzend Helfer —
/// zum Nachschlagen zu viel auf einmal. Aufgeteilt, nicht umgebaut: Es ist
/// dieselbe Klasse, nur in lesbaren Stuecken.
/// </summary>
internal sealed partial class MainWindow
{
    /// <summary>
    /// Werkzeuge, um herauszufinden, warum etwas nicht geht. Ein eigener Reiter,
    /// weil sie keine Einstellungen sind: Wer die Zielmenge aendern will, soll
    /// nicht an vier Knoepfen vorbei, die ins Protokoll schreiben.
    /// </summary>
    private void DrawDebugTab()
    {
        ImGui.TextDisabled("All of these write to /xllog. Nothing here changes what the plugin does.");
        ImGui.Spacing();

        using (ImRaiiDisabled(!_vendors.Ready))
        {
            if (ImGui.Button("Self-check"))
            {
                _restock.Refresh();
                SelfCheck.Run(_restock, _vendors);
                Notify("Self-check written to log.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Check every bait for missing sources or unreachable zones.");

        ImGui.SameLine();
        using (ImRaiiDisabled(!ShopWindowReader.IsOpen))
        {
            if (ImGui.Button("Dump shop"))
            {
                ShopWindowReader.DumpToLog();
                Notify("Shop window written to log.");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every entry of the open shop with its purchase index, price and currency.");

        ImGui.SameLine();
        if (ImGui.Button("Dump windows"))
        {
            AddonDump.Run();
            Notify("Open windows written to log.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("List every visible game window with its raw values. " +
                             "Useful when something is not recognised.");

        // Schritt eins der Gehilfen-Kette, allein pruefbar: Es bewegt nichts,
        // es faehrt nur hin und spricht die Glocke an. Was danach kommt —
        // Gehilfen auswaehlen, Inventar oeffnen, uebertragen — kommt erst,
        // wenn dieser Teil verlaesslich ist.
        var bell = SummoningBells.Nearest(_config, _vendors);

        using (ImRaiiDisabled(bell == null || _travel.Running || !_travel.Available))
        {
            if (ImGui.Button("Go to summoning bell") && bell is { } destination)
                _travel.Start(destination);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(bell is { } known
                ? $"Teleport to {known.AetheryteName} and walk to the summoning bell in {known.Zone}."
                : "No summoning bell known yet." + Environment.NewLine +
                  "There is no shipped table for them: a guessed position walks the character into " +
                  "a wall." + Environment.NewLine +
                  "Walk past one once and it is remembered — every city you visit adds another.");

        // Die vier Einzelschritte, aus der Aktionsleiste hierher gezogen: Sie
        // sind zum Pruefen da, nicht zum taeglichen Gebrauch. Dafuer gibt es
        // "Sort Bait Storage".
        StashButtons(Stash.Saddlebag, _saddleFetch, _saddleStow);
        StashButtons(Stash.Retainer, _retainerFetch, _retainerStow);

        // Teil zwei der Kette, einzeln pruefbar: eine Zeile anwaehlen und
        // sehen, ob die richtige aufgeht. Die Zahl ist einstellbar, weil ein
        // einzelner Mitschnitt nicht verraet, ob "param 1" die erste Zeile mit
        // Versatz oder die zweite von null gezaehlt ist.
        using (ImRaiiDisabled(!RetainerList.IsOpen))
        {
            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##retainerrow", ref _retainerRow);
            _retainerRow = Math.Clamp(_retainerRow, 0, 9);

            ImGui.SameLine();
            if (ImGui.Button("Open retainer"))
                _visit.Start(_retainerRow);
        }

        ImGui.SameLine();
        using (ImRaiiDisabled(!Stash.Retainer.Ready && !TopicSelect.IsOpen))
        {
            if (ImGui.Button("Leave retainer"))
                _visit.Leave();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Closes the inventory, picks Quit, clicks through the farewell and " +
                             "ends back at the list." + Environment.NewLine +
                             "The way out is the way in, backwards.");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(RetainerList.IsOpen
                ? "Selects that row of the open retainer list, counting from zero." +
                  Environment.NewLine +
                  "Try 0 and 1 and note which retainer opens — that settles how the parameter " +
                  "is counted."
                : "No retainer list open. Use a summoning bell first.");

        ImGui.SameLine();
        if (ImGui.Button(RetainerRecorder.Listening ? "Stop recording" : "Record retainer windows"))
            RetainerRecorder.Toggle();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Writes down what a real click on the retainer windows looks like: which window," +
                Environment.NewLine +
                "which event, which parameter." + Environment.NewLine +
                "Switch it on, open a bell, pick a retainer, choose the item menu, switch it off." +
                Environment.NewLine +
                "Guessing these has already cost this plugin two wrong answers and one lucky one.");

        ImGui.SameLine();
        if (ImGui.Button("Explain route"))
        {
            ExplainRoute();
            Notify("Route reasoning written to log.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("For every bait still missing: each vendor that sells it, whether it " +
                             "can be reached," + Environment.NewLine +
                             "what it costs there, and whether that is payable. The route is built " +
                             "from these answers." + Environment.NewLine +
                             "A bait that drops out of the route without a reason here is a bug.");

        ImGui.SameLine();
        if (ImGui.Button("Forget market board results"))
        {
            _market.ForgetUnlisted();
            Notify("Market board notes cleared.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears the notes about baits nothing was listed for, so every one is " +
                             "asked about " +
                             "again on the next run." + Environment.NewLine +
                             "They expire after three hours on their own.");

        ImGui.SameLine();
        if (ImGui.Button("Dump currencies"))
        {
            DumpCurrencies();
            Notify("Currency balances written to log.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Every currency a tracked bait is priced in, with what the plugin " +
                             "believes you hold." + Environment.NewLine +
                             "Compare it with the game: a number that reads 0 while you have some " +
                             "means the balance" + Environment.NewLine +
                             "is not kept where the plugin looks, and the route planning is wrong " +
                             "about that currency.");

        ImGui.SameLine();
        if (ImGui.Button("Export log to desktop"))
        {
            LogExport.Run(_config, _vendors, out var result);
            Notify(result);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes this plugin’s log lines to a text file on your desktop, " +
                             "with your settings at the top." + Environment.NewLine +
                             "Only lines from MasterBaiter — other plugins log character names " +
                             "and party data, and those stay out of the file.");

        if (_message.Length > 0 && DateTime.Now < _messageUntil)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_message);
        }

        ImGui.Spacing();
        ImGui.TextDisabled(_vendors.Ready
            ? $"{Tackle.LureCount} lures known, {_vendors.VendorCount} vendor entries, " +
              $"{Teleportable.Count} teleport destinations, " +
              $"{_retainers.Coverage().Seen} of {_retainers.Coverage().Total} retainers counted, " +
              $"{SummoningBells.KnownCount(_config)} summoning bell(s) known, " +
              (_restock.SaddlebagRead ? "saddlebag counted." : "saddlebag not counted yet.")
            : "Building the vendor index...");
    }

    /// <summary>
    /// Schreibt fuer jeden fehlenden Koeder auf, was die Routenplanung ueber
    /// ihn weiss.
    ///
    /// Seit die Planung den Geldbeutel einbezieht, kann ein Koeder aus der
    /// Route fallen, ohne dass man sieht warum — an der Erreichbarkeit, am
    /// Preis, an der Waehrung oder am Bestand. Die Entscheidung besteht aus
    /// vier Angaben, also stehen hier alle vier.
    /// </summary>
    private void ExplainRoute()
    {
        var open = _restock.Rows.Where(r => !r.Ignored && r.Missing > 0).ToList();
        if (open.Count == 0)
        {
            Plugin.Log.Information("[MasterBaiter] Nothing is missing, so there is nothing to explain.");
            return;
        }

        Plugin.Log.Information($"[MasterBaiter] Route reasoning for {open.Count} missing bait(s):");

        foreach (var row in open)
        {
            var vendorList = _vendors.For(row.BaitId);
            if (vendorList.Count == 0)
            {
                Plugin.Log.Information($"[MasterBaiter]   {row.Name} x{row.Missing}: no vendor at all.");
                continue;
            }

            Plugin.Log.Information(
                $"[MasterBaiter]   {row.Name} x{row.Missing}: {vendorList.Count} vendor(s), " +
                $"prices: {string.Join(" | ", _vendors.PricesFor(row.BaitId))}");

            // Je Ladenart genuegt ein Vertreter — zwoelf Gil-Haendler
            // beantworten dieselbe Frage zwoelfmal.
            foreach (var kind in vendorList.Select(v => v.Kind).Distinct())
            {
                var vendor = vendorList.First(v => v.Kind == kind);
                var reachable = Reach.CanReach(vendor, out var why);

                var price = _vendors.PricesFor(row.BaitId)
                    .Select(t => PriceTag.TryParse(t, out var tag) ? tag : (PriceTag?)null)
                    .FirstOrDefault(t => t?.FitsVendor(kind) == true);

                var cost = price is { } p ? p.TotalFor(row.Missing) : 0;
                var held = price is { } q ? Wallet.Balance(q.Currency) : null;

                Plugin.Log.Information(
                    $"[MasterBaiter]     [{vendor.KindName}] {vendor.Npc} in {vendor.Zone}: " +
                    (reachable ? "reachable" : $"NOT reachable ({why})") + ", " +
                    (price is { } r
                        ? $"{cost:N0} {r.Currency} needed, {held?.ToString() ?? "unknown"} held"
                        : "no price in a currency this vendor takes"));
            }
        }
    }

    /// <summary>
    /// Schreibt auf, welche Waehrungen ueberhaupt vorkommen und wie viel das
    /// Plugin davon zu sehen glaubt.
    ///
    /// Die Routenplanung entscheidet daran, wohin gefahren wird. Liest sie eine
    /// Waehrung falsch, sieht das Ergebnis wie eine gute Entscheidung aus —
    /// deshalb muss die Zahl nachpruefbar sein, statt nur zu wirken.
    /// </summary>
    private void DumpCurrencies()
    {
        var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _restock.Rows)
        foreach (var text in _vendors.PricesFor(row.BaitId))
            if (PriceTag.TryParse(text, out var tag))
                currencies.Add(tag.Currency);

        if (currencies.Count == 0)
        {
            Plugin.Log.Information("[MasterBaiter] No priced bait, so no currencies to report.");
            return;
        }

        foreach (var currency in currencies)
        {
            var id = Wallet.IdOf(currency);
            if (id == null)
            {
                Plugin.Log.Information(
                    $"[MasterBaiter] Currency \"{currency}\": no item id found — treated as unlimited.");
                continue;
            }

            // Beides nebeneinander: Weichen sie ab, ist die Waehrung hier nicht
            // lesbar und die Planung arbeitet mit dem Gedaechtnis.
            var live = Restock.CountInInventory(id.Value);
            if (live > 0)
                Wallet.Remember(id.Value, live);

            var kept = Wallet.Remembered(id.Value);
            var seen = Wallet.SeenAt(id.Value);

            Plugin.Log.Information(
                $"[MasterBaiter] Currency \"{currency}\" (item {id}): {live} readable here, " +
                (kept == null
                    ? "nothing remembered."
                    : $"{kept} remembered{(seen is { } when ? $" from {when:HH:mm}" : string.Empty)}."));
        }
    }
}
