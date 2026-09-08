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
    ///
    /// Drei Abschnitte, geordnet danach, was ein Knopf anrichtet: nachsehen,
    /// etwas bewegen, etwas vergessen. Vorher standen alle elf in einer einzigen
    /// Reihe, die hinter dem rechten Fensterrand endete — ausgerechnet mit
    /// "Export log" ganz aussen, wonach gefragt wird, wenn etwas schiefging.
    /// </summary>
    private void DrawDebugTab()
    {
        Section("Look");
        ImGui.TextDisabled("These only read, and write what they find to /xllog. " +
                           "Nothing here moves anything.");
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
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Check every bait for missing sources or unreachable zones.");

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

        using (ImRaiiDisabled(!ShopWindowReader.IsOpen))
        {
            if (ImGui.Button("Dump shop"))
            {
                ShopWindowReader.DumpToLog();
                Notify("Shop window written to log.");
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ShopWindowReader.IsOpen
                ? "Every entry of the open shop with its purchase index, price and currency."
                : "No shop window open. Talk to a vendor first.");

        ImGui.SameLine();
        if (ImGui.Button("Dump windows"))
        {
            AddonDump.Run();
            Notify("Open windows written to log.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("List every visible game window with its raw values. " +
                             "Useful when something is not recognised.");

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

        Section("Bait storage, one step at a time");
        ImGui.TextDisabled("The steps that \"Sort Bait Storage\" runs in order. " +
                           "Use them when the whole run stops somewhere.");
        ImGui.Spacing();

        // Schritt eins der Gehilfen-Kette, allein pruefbar: Es bewegt nichts,
        // es faehrt nur hin und spricht die Glocke an.
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

        // Schritt zwei, einzeln pruefbar: eine Zeile anwaehlen und sehen, ob die
        // richtige aufgeht. Die Zahl bleibt einstellbar, weil ein einzelner
        // Mitschnitt nicht verraet, wie das Spiel die Zeilen zaehlt.
        ImGui.SameLine();
        using (ImRaiiDisabled(!RetainerList.IsOpen))
        {
            ImGui.SetNextItemWidth(70);
            ImGui.InputInt("##retainerrow", ref _retainerRow);
            _retainerRow = Math.Clamp(_retainerRow, 0, 9);

            ImGui.SameLine();
            if (ImGui.Button("Open retainer"))
                _visit.Start(_retainerRow);
        }
        // Hier hingen einmal zwei Hinweistexte am selben Knopf: Der Text fuer
        // "Open retainer" stand hinter dem fuer "Leave retainer" und hat ihn
        // ueberschrieben. Der eine Knopf zeigte den falschen, der andere keinen.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(RetainerList.IsOpen
                ? "Selects that row of the open retainer list, counting from zero."
                : "No retainer list open. Use a summoning bell first.");

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

        // Beide Zeilen teilen sich eine Spaltenkante, damit "Deposit" nicht
        // einmal hier und einmal dort anfaengt: Der laengere der beiden linken
        // Knoepfe gibt sie vor.
        var column = Math.Max(WithdrawWidth(Stash.Saddlebag), WithdrawWidth(Stash.Retainer))
                     + ImGui.GetStyle().ItemSpacing.X;

        StashButtons(Stash.Saddlebag, _saddleFetch, _saddleStow, column);
        StashButtons(Stash.Retainer, _retainerFetch, _retainerStow, column);

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

        Section("Log");

        var detailed = _config.DetailedLog;
        if (ImGui.Checkbox("Detailed log", ref detailed))
        {
            _config.DetailedLog = detailed;
            _config.Save();
            Trace.Detailed = detailed;
            Plugin.Log.Information(
                $"[MasterBaiter] Detailed log switched {(detailed ? "on" : "off")}.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Writes every state change to /xllog: which step each part is in, which windows " +
                "are open," + Environment.NewLine +
                "which zone you are in, what you hold, how much room is left, and which button " +
                "you pressed." + Environment.NewLine +
                "Transitions only, not frames — a whole route run is a few hundred lines." +
                Environment.NewLine +
                "On by default: a log you switch on after something went wrong helps with the " +
                "next one, not this one.");

        Section("Forget");

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

        // Ganz unten, weil beides dasselbe beantwortet: was das Plugin gerade
        // weiss. Die Rueckmeldung eines Knopfes hat vier Sekunden lang Vorrang
        // vor dem Dauerzustand.
        ImGui.Spacing();
        ImGui.Separator();

        if (_message.Length > 0 && DateTime.Now < _messageUntil)
            ImGui.TextColored(_config.HoneyTheme ? Honey : new Vector4(0.6f, 0.8f, 1f, 1f), _message);
        else
            ImGui.TextDisabled(_vendors.Ready
                ? $"{Tackle.LureCount} lures known, {_vendors.VendorCount} vendor entries, " +
                  $"{Teleportable.Count} teleport destinations, " +
                  $"{_retainers.Coverage().Seen} of {_retainers.Coverage().Total} retainers counted, " +
                  $"{SummoningBells.KnownCount(_config)} summoning bell(s) known, " +
                  (_restock.SaddlebagRead ? "saddlebag counted." : "saddlebag not counted yet.")
                : "Building the vendor index...");
    }

    /// <summary>Wie breit der "Withdraw"-Knopf dieses Lagers ist.</summary>
    private static float WithdrawWidth(Stash stash) =>
        ImGui.CalcTextSize($"Withdraw Bait from {stash.Name}").X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>
    /// Die beiden Einzelschritte eines Lagers, nebeneinander.
    ///
    /// Sie standen einmal in der Aktionsleiste; seit "Sort Bait Storage" beides
    /// in einem Durchlauf erledigt, sind sie nur noch zum Pruefen da und wohnen
    /// deshalb hier. <paramref name="column"/> ist die gemeinsame Kante fuer den
    /// rechten Knopf, damit Satteltasche und Gehilfe untereinander stehen.
    /// </summary>
    private void StashButtons(Stash stash, string? fetchBlocker, string? stowBlocker, float column)
    {
        var busy = _stash.Running;

        using (ImRaiiDisabled(busy || fetchBlocker != null))
        {
            if (ImGui.Button($"Withdraw Bait from {stash.Name}"))
            {
                Trace.Pressed($"Withdraw Bait from {stash.Name}");
                _stash.Start(_restock, stash);
            }
        }
        // Auch im ausgegrauten Zustand: Gerade dann steht im Hinweistext,
        // warum der Knopf nicht geht — und gerade dann fragt man danach.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(fetchBlocker ??
                $"Moves bait you are missing out of the {stash.Name.ToLowerInvariant()} and into " +
                "your bags." + Environment.NewLine +
                "Whole stacks only: the game moves stacks, not amounts, so one larger than the " +
                "gap stays" + Environment.NewLine +
                "unless your bags hold none of it at all.");

        ImGui.SameLine(column);
        using (ImRaiiDisabled(busy || stowBlocker != null))
        {
            if (ImGui.Button($"Deposit Bait in {stash.Name}"))
            {
                Trace.Pressed($"Deposit Bait in {stash.Name}");
                _stash.StartStow(_restock, stash);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(stowBlocker ??
                "Puts away bait that no fish on your list needs any more." + Environment.NewLine +
                "Having more than the target is not a reason: that surplus is yours to keep at " +
                "hand." + Environment.NewLine +
                "Whole stacks only, and never one that would drop you below the target.");
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
