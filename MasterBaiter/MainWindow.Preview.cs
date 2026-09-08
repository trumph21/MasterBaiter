using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MasterBaiter;

/// <summary>
/// Die Routenvorschau.
///
/// Teil von <see cref="MainWindow"/>. Die Datei war auf zweitausend Zeilen
/// gewachsen und enthielt vier Reiter, die Vorschau und ein Dutzend Helfer —
/// zum Nachschlagen zu viel auf einmal. Aufgeteilt, nicht umgebaut: Es ist
/// dieselbe Klasse, nur in lesbaren Stuecken.
/// </summary>
internal sealed partial class MainWindow
{
    /// <summary>
    /// Was die Route vorhat, bevor sie losfaehrt: jeder Halt, was dort gekauft
    /// wird und was es kostet.
    ///
    /// Der Hinweistext am Knopf konnte das nicht: Er verschwindet beim
    /// Wegsehen, fasst nach sechs Koedern zusammen und nennt keine Preise. Was
    /// ein Durchlauf kostet, sollte man vorher wissen und nicht hinterher.
    /// </summary>
    private void DrawRoutePreview()
    {
        if (_openPreview)
        {
            ImGui.OpenPopup("Route preview");
            _openPreview = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Route preview", ref open, ImGuiWindowFlags.NoSavedSettings))
            return;

        var plan = CachedPlan();
        var byName = _restock.Rows.ToDictionary(r => r.Name, r => r);
        var totals = new Dictionary<string, long>();
        var unknown = 0;
        var items = 0;

        ImGui.BeginChild("##stops", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2.4f));

        for (var i = 0; i < plan.Count; i++)
        {
            var stop = plan[i];

            ImGui.TextColored(_config.HoneyTheme ? Honey : new Vector4(0.6f, 0.8f, 1f, 1f),
                $"{i + 1}. {stop.Vendor.Npc}");
            ImGui.SameLine();
            ImGui.TextColored(Dim, $"{stop.Vendor.Zone} [{stop.Vendor.KindName}]");

            if (ImGui.BeginTable($"##stop{i}", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn("bait", ImGuiTableColumnFlags.WidthStretch, 2.4f);
                ImGui.TableSetupColumn("count", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("cost", ImGuiTableColumnFlags.WidthStretch, 1.6f);

                foreach (var name in stop.Baits)
                {
                    if (!byName.TryGetValue(name, out var row))
                        continue;

                    items += row.Missing;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(name);

                    ImGui.TableNextColumn();
                    Centered(row.Missing.ToString(), Wanted);

                    ImGui.TableNextColumn();

                    // Am Marktbrett steht der Preis erst fest, wenn gefragt
                    // wurde. Vorher zu schaetzen waere geraten.
                    PriceTag? found = null;
                    string? wrongCurrency = null;

                    if (stop.IsMarketBoard)
                    {
                        if (_market.QuoteFor(row.BaitId) is { UnitPrice: > 0 } quote
                            && PriceTag.TryParse($"{quote.UnitPrice} Gil", out var boardTag))
                            found = boardTag;
                    }
                    else
                    {
                        // Alle bekannten Preise, aber nur der zaehlt, dessen
                        // Waehrung dieser Halt nimmt.
                        foreach (var candidate in _vendors.PricesFor(row.BaitId))
                        {
                            if (!PriceTag.TryParse(candidate, out var tag))
                                continue;

                            if (tag.FitsVendor(stop.Vendor.Kind))
                            {
                                found = tag;
                                break;
                            }

                            wrongCurrency ??= tag.Currency;
                        }
                    }

                    if (found is { } price)
                    {
                        var cost = price.TotalFor(row.Missing);
                        totals[price.Currency] = totals.GetValueOrDefault(price.Currency) + cost;
                        Priced($"{cost:N0} {price.Currency}", Dim);
                    }
                    else
                    {
                        unknown++;
                        Centered("?", Dim);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(stop.IsMarketBoard
                                ? "Market board prices are only known once the board has been asked."
                                : wrongCurrency != null
                                    ? $"Only a price in {wrongCurrency} is known, and this vendor does not take that."
                                    : "No price in the game data for this one.");
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
        }

        ImGui.EndChild();

        ImGui.Separator();
        ImGui.TextUnformatted(plan.Count == 1
            ? $"1 stop, {items} items"
            : $"{plan.Count} stops, {items} items");

        if (totals.Count > 0)
        {
            var sums = totals.OrderByDescending(t => t.Value)
                .Select(t => $"{t.Value:N0} {t.Key}")
                .ToList();

            ImGui.SameLine();
            ImGui.TextColored(Dim, "-  " + string.Join(", ", sums.Select(Shorten)));

            // Dieselbe Auskunft wie in der Kostenspalte: Wer "1.926 Purple GS"
            // liest und nicht vorher eine Zeile darueber angefahren ist,
            // erfaehrt sonst nirgends, wofuer das Kuerzel steht.
            if (ImGui.IsItemHovered() && sums.Any(sum => Shorten(sum) != sum))
                ImGui.SetTooltip(string.Join(Environment.NewLine, sums));
        }

        if (unknown > 0)
        {
            ImGui.TextColored(Dim, unknown == 1
                ? "1 bait has no known price, so it is not in the total."
                : $"{unknown} baits have no known price, so they are not in the total.");
        }

        // Seit die Planung den Geldbeutel kennt, faellt ein Koeder aus der
        // Route, wenn keine erreichbare Quelle bezahlbar ist. Stillschweigend
        // waere das die schlechtere Haelfte der Verbesserung: Man saehe nur,
        // dass er fehlt, nicht warum.
        var planned = plan.SelectMany(stop => stop.Baits).ToHashSet();
        var left = _restock.Rows
            .Where(r => !r.Ignored && r.Missing > 0 && !planned.Contains(r.Name))
            .ToList();

        if (left.Count > 0)
        {
            ImGui.TextColored(Wanted, left.Count == 1
                ? "1 bait is not in this route."
                : $"{left.Count} baits are not in this route.");

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "No vendor you can reach takes a currency you have enough of." +
                    Environment.NewLine +
                    "They come back into the route once you have the currency:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, left.Take(15).Select(r => r.Name)) +
                    (left.Count > 15 ? Environment.NewLine + $"and {left.Count - 15} more" : string.Empty));
        }

        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running
                              || _market.Running || !_travel.Available))
        {
            if (ImGui.Button("Start route"))
            {
                _route.Start();
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }
}
