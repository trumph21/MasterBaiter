using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MasterBaiter;

/// <summary>
/// Der Reiter "Baits": die Tabelle und alles, was ueber ihr steht.
///
/// Teil von <see cref="MainWindow"/>. Die Datei war auf zweitausend Zeilen
/// gewachsen und enthielt vier Reiter, die Vorschau und ein Dutzend Helfer —
/// zum Nachschlagen zu viel auf einmal. Aufgeteilt, nicht umgebaut: Es ist
/// dieselbe Klasse, nur in lesbaren Stuecken.
/// </summary>
internal sealed partial class MainWindow
{
    private void DrawBaitsTab()
    {
        DrawActionBar();
        DrawWarnings();
        ImGui.Separator();

        if (_restock.Status != null)
        {
            ImGui.TextWrapped($"Could not read the list: {_restock.Status}");
            return;
        }

        if (_restock.Rows.Count == 0)
        {
            ImGui.TextWrapped("No fish in the auto-gather list need bait.");
            return;
        }

        DrawFilter();
        DrawTable();
        DrawRoutePreview();
    }

    /// <summary>
    /// Nur was man im Spielgeschehen braucht. Alles, was man einmal einstellt
    /// und dann vergisst, steht im anderen Reiter.
    /// </summary>
    private void DrawActionBar()
    {
        if (ImGui.Button("Refresh"))
        {
            Trace.Pressed("Refresh");
            _restock.Refresh();
        }

        ImGui.SameLine();

        var shopOpen = ShopWindowReader.IsOpen;

        // Kein "Buy missing" mehr. Gekauft wird, wo das Plugin selbst
        // hingefahren ist — ueber "Run route" oder "Go" mit "buy on arrival".
        // Fuer den Einzelfall am offenen Laden bleibt der Buy-Knopf in der
        // jeweiligen Zeile.
        if (_queue.Running || _sweep.Running)
        {
            if (ImGui.Button("Cancel"))
            {
                _sweep.Stop("Stopped.");
                _queue.Stop("Stopped.");
            }

            ImGui.SameLine();
        }

        // Kein eigener Marktbrett-Knopf mehr: Die Route nimmt das Brett als
        // letzten Halt mit, und "buy on arrival" loest den Kauf dort aus. Ein
        // Knopf, der nur greift, waehrend man selbst davorsteht, war die
        // Ausnahme und nicht die Regel.
        if (_market.Running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop market board"))
                _market.Stop("Stopped.");
        }

        ImGui.SameLine();
        if (_route.Running)
        {
            if (ImGui.Button("Stop route"))
                _route.Stop("Route stopped.");
        }
        else
        {
            var plan = CachedPlan();
            using (ImRaiiDisabled(plan.Count == 0 || _travel.Running || _queue.Running
                                  || _sweep.Running || _market.Running || !_travel.Available))
            {
                if (ImGui.Button(plan.Count > 0 ? $"Run route ({plan.Count} stops)" : "Run route"))
                {
                    Trace.Pressed($"Run route, {plan.Count} stop(s)");
                    _route.Start();
                }
            }
            if (plan.Count > 0 && ImGui.IsItemHovered())
            {
                // Die Ladenart gehoert dazu, sonst widerspricht diese Liste der
                // Haendlerliste in der Tabelle, ohne dass man den Grund sieht:
                // Dort steht die guenstigere Waehrung oben, hier gewinnt, wer
                // die meisten Koeder in einem Halt abdeckt.
                var lines = plan.Select((s, i) =>
                    $"{i + 1}. {s.Vendor.Npc} - {s.Vendor.Zone} [{s.Vendor.KindName}]: " +
                    string.Join(", ", s.Baits.Take(6)) +
                    (s.Baits.Count > 6 ? $" (+{s.Baits.Count - 6})" : string.Empty)).ToList();

                lines.Add(string.Empty);
                lines.Add("Fewest stops wins, so a vendor covering more baits beats a cheaper currency.");

                ImGui.SetTooltip(string.Join(Environment.NewLine, lines));
            }

            ImGui.SameLine();
            using (ImRaiiDisabled(plan.Count == 0))
            {
                if (ImGui.Button("Preview"))
                    _openPreview = true;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(plan.Count == 0
                    ? "Nothing to plan: no bait is missing that a vendor you can reach and pay could supply."
                    : "Every stop with what it buys and what it costs, before anything moves.");
        }

        if (_travel.Running && !_route.Running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop travel"))
                _travel.Stop("Travel stopped.");
        }

        // Immer sichtbar, notfalls ausgegraut mit Begruendung: Ein fehlender
        // Knopf sagt nicht, ob nichts zu tun ist oder etwas nicht geht.
        RefreshBlockers();

        // Ein Knopf fuer den ganzen Gang. Die vier Einzelschritte stehen im
        // Debug-Reiter — wer einen davon pruefen will, findet sie dort, aber
        // die Leiste bleibt lesbar.
        var runBlocker = _retainerRun.Running ? null : _retainerRun.Blocker();

        ImGui.SameLine();
        if (_retainerRun.Running)
        {
            if (ImGui.Button("Stop sorting"))
                _retainerRun.Stop("Stopped.");
        }
        else
        {
            using (ImRaiiDisabled(runBlocker != null || _stash.Running || _visit.Running))
            {
                if (ImGui.Button("Sort Bait Storage"))
                {
                    Trace.Pressed("Sort Bait Storage");
                    _retainerRun.Start();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(runBlocker ??
                    "Goes through every place your bait sits:" + Environment.NewLine +
                    "the saddlebag first, since it opens anywhere, then a summoning bell and each " +
                    "retainer." + Environment.NewLine +
                    "At each one: fetch what your bags are short of, put away what no fish needs " +
                    "any more." + Environment.NewLine +
                    "Retainers left over from a shrunk subscription are skipped without shifting " +
                    "the rest." +
                    // Ein Vorbehalt, kein Hindernis: Er sagt, was dieser Gang
                    // auslaesst, und nicht, dass es keinen gibt.
                    (_retainerRun.Caveat() is { } caveat
                        ? Environment.NewLine + Environment.NewLine + caveat
                        : string.Empty));
        }

        ImGui.SameLine();
        ImGui.TextDisabled(shopOpen ? "vendor open" : "no vendor open");

        var status = CurrentStatus();
        if (status.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(status);
        }
        else if (_message.Length > 0 && DateTime.Now < _messageUntil)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted(_message);
        }

        DrawPurse();
    }

    /// <summary>Was gerade laeuft, in der Reihenfolge der Dringlichkeit.</summary>
    private string CurrentStatus()
    {
        if (_sweep.Running && _sweep.Status.Length > 0)
            return _sweep.Status;
        if (_market.Running && _market.Status.Length > 0)
            return _market.Status;
        if (_retainerRun.Running && _retainerRun.Status.Length > 0)
            return _retainerRun.Status;
        if (_visit.Running && _visit.Status.Length > 0)
            return _visit.Status;
        if (_stash.Running && _stash.Status.Length > 0)
            return _stash.Status;
        if (_queue.Status.Length > 0)
            return _queue.Status;
        if (_route.Running && _route.Status.Length > 0)
            return _route.Status;
        return _travel.Status;
    }

    private void DrawWarnings()
    {
        var missingHelpers = _travel.MissingHelpers();
        if (missingHelpers.Count > 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                $"Missing: {string.Join(" and ", missingHelpers)} - required for Go and Run route.");

        if (!_vendors.Ready || _restock.Rows.Count == 0)
            return;

        // Nur die Koeder, die deine Fische brauchen. Sind alle aufgelistet,
        // waeren sonst hundert Event- und Belohnungskoeder ohne Quelle dabei,
        // und die Zeile sähe nach einem Rueckschritt aus, obwohl nichts fehlt.
        var relevant = _restock.Rows.Where(r => r.Needed).ToList();
        var orphans = relevant.Where(r => _vendors.For(r.BaitId).Count == 0
                                          && _vendors.OtherSourcesFor(r.BaitId).Count == 0
                                          && _vendors.RecipeFor(r.BaitId) == null).ToList();

        ImGui.TextDisabled(orphans.Count == 0
            ? $"All {relevant.Count} baits your fish need can be bought or crafted."
            : $"{orphans.Count} of {relevant.Count} needed baits have no shop and no recipe.");
        if (orphans.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join(Environment.NewLine, orphans.Take(20).Select(r => r.Name)));

        // Der Vorrat bei den Gehilfen laesst sich nicht erfragen, nur
        // mitschreiben. Wer das nicht weiss, haelt eine fehlende Zahl fuer
        // eine Null — also einmal sagen, was zu tun ist, statt es im
        // Hinweistext zu verstecken.
        if (!_config.CountRetainers)
            return;

        var (total, seen) = _retainers.Coverage();

        if (total > 0 && seen >= total)
            return;
        if (total == 0 && seen > 0)
            return;

        ImGui.TextColored(Wanted, total > 0
            ? seen == 0
                ? $"None of your {total} retainers have been counted yet."
                : $"{total - seen} of {total} retainers have not been counted yet."
            : "No retainer has been counted yet.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "The game only hands out a retainer's contents while that retainer is open." +
                Environment.NewLine +
                "Open each one once at a summoning bell and the bait in it counts towards" +
                Environment.NewLine +
                "your target, so it is not bought a second time.");
    }

    /// <summary>
    /// Was im Beutel ist, rechts in der Leiste.
    ///
    /// Es ist die Zahl, an der die Routenplanung haengt: Ohne Scrips faellt
    /// der Scrip-Halt weg, und ohne diese Anzeige waere das eine stille
    /// Entscheidung, deren Grund man nirgends sieht.
    ///
    /// Ein gemerkter Wert steht gedaempft — er ist eine Erinnerung, keine
    /// Ablesung, und das soll man sehen koennen, ohne den Hinweistext zu
    /// oeffnen.
    /// </summary>
    private void DrawPurse()
    {
        var purse = CachedPurse();
        if (purse.Count == 0)
            return;

        var parts = purse.Select(h => Shorten($"{Compact(h.Amount)} {h.Currency}")).ToList();
        var width = ImGui.CalcTextSize(string.Join("   ", parts)).X;

        ImGui.SameLine();
        var space = ImGui.GetContentRegionAvail().X;
        if (space > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + space - width);

        var gap = ImGui.CalcTextSize(" ").X;

        for (var i = 0; i < purse.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, ImGui.CalcTextSize("   ").X);

            var holding = purse[i];

            // Die Zahl weiss, der Waehrungsname in seiner eigenen Farbe: Die
            // Scrips heissen nach ihrer Farbe, also ist sie das schnellste
            // Erkennungsmerkmal — schneller als der gelesene Name.
            //
            // Eine gemerkte Zahl steht grau statt weiss. Der Unterschied
            // zwischen Ablesung und Erinnerung darf nicht verlorengehen, nur
            // weil die Farbe jetzt die Waehrung bezeichnet.
            var split = parts[i].IndexOf(' ');
            var number = split < 0 ? parts[i] : parts[i][..split];
            var name = split < 0 ? string.Empty : parts[i][(split + 1)..];

            ImGui.BeginGroup();
            ImGui.TextColored(holding.Live ? White : Dim, number);

            if (name.Length > 0)
            {
                ImGui.SameLine(0, gap);
                ImGui.TextColored(CurrencyColour(holding.Currency), name);
            }

            ImGui.EndGroup();

            if (!ImGui.IsItemHovered())
                continue;

            // Nur der Sonderfall braucht einen Satz. "Readable here" stand
            // unter jeder der vier Waehrungen und sagte nichts, was die weisse
            // Zahl nicht schon sagt.
            ImGui.SetTooltip($"{holding.Amount:N0} {holding.Currency}" +
                (holding.Live
                    ? string.Empty
                    : Environment.NewLine +
                      (holding.Seen is { } when
                          ? $"Not readable here — remembered from {Ago(when)}."
                          : "Not readable here.")));
        }
    }

    /// <summary>Grosse Zahlen kurz: 17.705.069 wird zu 17.7M.</summary>
    private static string Compact(int amount) => amount switch
    {
        >= 1_000_000 => $"{amount / 1_000_000.0:0.#}M",
        >= 10_000 => $"{amount / 1000.0:0.#}k",
        _ => amount.ToString("N0"),
    };

    private List<Purse.Holding> CachedPurse()
    {
        var now = Environment.TickCount64;
        if (now < _purseAt)
            return _purse;

        _purseAt = now + 1000;
        _purse = Purse.Held(_restock.Rows.SelectMany(r => _vendors.PricesFor(r.BaitId)));
        return _purse;
    }

    /// <summary>
    /// Die Farbe einer Waehrung.
    ///
    /// Die Scrips tragen ihre Farbe im Namen, also wird sie danach vergeben —
    /// keine Liste bekannter Waehrungen, die jede Erweiterung veraltet, sondern
    /// die Regel, nach der das Spiel sie benennt.
    /// </summary>
    private static Vector4 CurrencyColour(string currency)
    {
        bool Has(string word) => currency.Contains(word, StringComparison.OrdinalIgnoreCase);

        if (Has("cosmocredit") || Has("lunar credit")) return Cyan;
        if (Has("orange")) return Orange;
        if (Has("purple")) return Purple;
        if (Has("red")) return Crimson;
        if (Has("blue")) return Azure;
        if (Has("yellow") || Has("gil")) return Honey;
        if (Has("white")) return White;

        return Dim;
    }

    /// <summary>Suchfeld ueber der Tabelle.</summary>
    private void DrawFilter()
    {
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##filter", "Search", ref _filter, 64);

        if (_filter.Length == 0)
            return;

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            _filter = string.Empty;

        ImGui.SameLine();
        var shown = _restock.Rows.Count(Matches);
        ImGui.TextColored(Dim, shown == 1
            ? $"1 of {_restock.Rows.Count} baits"
            : $"{shown} of {_restock.Rows.Count} baits");
    }

    private bool Matches(Restock.Row row) =>
        _filter.Length == 0 || row.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Die Zeilen, wie sie gerade zu zeigen sind: gefiltert und in der
    /// Reihenfolge, die in der Kopfzeile angeklickt wurde.
    /// </summary>
    private unsafe List<Restock.Row> Visible()
    {
        var rows = _restock.Rows.Where(Matches).ToList();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount == 0)
            return rows;

        var spec = specs.Specs[0];
        var up = spec.SortDirection == ImGuiSortDirection.Ascending;

        Comparison<Restock.Row> by = spec.ColumnIndex switch
        {
            1 => (a, b) => a.Fish.Count.CompareTo(b.Fish.Count),
            2 => (a, b) => a.Have.CompareTo(b.Have),
            3 => (a, b) => a.Bag.CompareTo(b.Bag),
            4 => (a, b) => a.Target.CompareTo(b.Target),
            5 => (a, b) => a.Missing.CompareTo(b.Missing),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        // Bei gleichem Wert nach Namen, sonst springen Zeilen mit gleicher
        // Fehlmenge bei jedem Bild umher.
        rows.Sort((a, b) =>
        {
            var order = by(a, b);
            if (order != 0)
                return up ? order : -order;

            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return rows;
    }

    private void DrawTable()
    {
        // Waagerechte Linien statt eines Gitters: Die senkrechten Striche
        // zerschneiden eine Zeile, die ohnehin von links nach rechts gelesen
        // wird.
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
                                      | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp
                                      | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.Sortable;

        if (!ImGui.BeginTable("##baits", 8, flags))
            return;

        // Die Haendlerspalte waechst mit, statt fest zu bleiben: Bei
        // "13 vendors" plus Knopf reichten neunzig Punkte nicht, und der Knopf
        // wurde am rechten Rand abgeschnitten.
        ImGui.TableSetupColumn("Bait", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Fish", ImGuiTableColumnFlags.WidthFixed, 54);

        // "Have" zaehlte Beutel, Satteltasche und Gehilfen zusammen — richtig
        // fuers Nachkaufen, irrefuehrend fuers Angeln: 504 Red Maggots in der
        // Satteltasche sahen aus wie ein voller Vorrat, waehrend der Beutel
        // leer war. Beide Zahlen stehen jetzt nebeneinander.
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Bag", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("Missing",
            ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort
            | ImGuiTableColumnFlags.PreferSortDescending, 74);
        // Der Preis ist "5 gil" bis "1920 gil" — mitwachsend war er meist zu
        // drei Vierteln leer. Fest, und der Platz geht an den Koedernamen.
        // Preis und Haendler bleiben unsortierbar: Der Preis kommt je nach
        // Zeile aus dem offenen Laden, den Spieldaten oder dem Marktbrett und
        // ist mal Gil, mal Scrips — eine Reihenfolge daraus waere erfunden.
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.5f);
        ImGui.TableSetupColumn("Vendor", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.2f);
        ImGui.TableSetupScrollFreeze(0, 1);
        CenteredHeadersRow(8, 1, 6);

        foreach (var row in Visible())
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)row.BaitId);

            ImGui.TableNextColumn();
            var ignored = row.Ignored;
            if (ImGui.Checkbox("##ign", ref ignored))
            {
                if (ignored) _config.Ignored.Add(row.BaitId);
                else _config.Ignored.Remove(row.BaitId);
                row.Ignored = ignored;
                _config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Check to skip this bait when buying.");
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Name);

            ImGui.TableNextColumn();
            if (row.Needed)
            {
                Centered(row.Fish.Count.ToString(), Dim);
                if (ImGui.IsItemHovered() && row.Fish.Count > 0)
                    ImGui.SetTooltip(string.Join("\n", row.Fish.Take(20).Select(Restock.ItemName)));
            }
            else
            {
                Centered("-", Dim);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("No fish on your list needs this one.");
            }

            // Die Summe, und darunter woraus sie besteht. Der Breakdown gehoert
            // hierher und nicht an "Bag": Er erklaert, wie die Gesamtzahl
            // zustande kommt — an der Beutelzahl beantwortete er eine Frage,
            // die dort niemand stellt.
            ImGui.TableNextColumn();
            Centered(row.Have.ToString(), row.Missing > 0 ? null : Dim);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(StockBreakdown(row));

            // Im Beutel: die Zahl, mit der man tatsaechlich angeln kann.
            ImGui.TableNextColumn();
            // Fehlt im Beutel etwas, das anderswo liegt, sagt "Missing" nichts
            // davon — dort steht null, weil nichts zu kaufen ist. Dann muss es
            // diese Zahl sagen.
            var shortInBag = row.Target - row.Bag;
            var elsewhere = shortInBag > 0 && row.Have >= row.Target;

            Centered(row.Bag.ToString(),
                elsewhere ? Wanted : shortInBag > 0 ? null : Dim);
            // Nur einer. Hier standen zwei SetTooltip hintereinander am selben
            // Feld, und der zweite gewinnt immer — der Grund fuer die gelbe
            // Zahl war deshalb nie zu sehen.
            if (elsewhere && ImGui.IsItemHovered())
                ImGui.SetTooltip($"{shortInBag} of these are not in your bags — " +
                                 "you own them, but you cannot fish with them there.");

            // Zwanzig Eingabekaesten untereinander waren das lauteste in der
            // Tabelle, und ihre Zahl stand als einzige links, waehrend die
            // Nachbarspalten rechts standen. Jetzt steht hier eine Zahl wie in
            // jeder anderen Spalte, und das Feld erscheint erst beim Anklicken.
            ImGui.TableNextColumn();
            var target = row.Target;
            if (_editing == row.BaitId)
            {
                if (_grabFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _grabFocus = false;
                }

                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputInt("##target", ref target, 0, 0, "%d", ImGuiInputTextFlags.AutoSelectAll))
                {
                    target = Math.Clamp(target, 0, 9999);
                    if (target == _config.DefaultTarget) _config.Targets.Remove(row.BaitId);
                    else _config.Targets[row.BaitId] = target;
                    row.Target = target;
                    _config.Save();
                }

                if (ImGui.IsItemDeactivated())
                    _editing = 0;
            }
            else
            {
                ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0f));
                if (ImGui.Selectable($"{target}##target", false))
                {
                    _editing = row.BaitId;
                    _grabFocus = true;
                }
                ImGui.PopStyleVar();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Click to edit");
            }

            ImGui.TableNextColumn();
            // Die Fehlmenge ist die Zahl, wegen der man das Fenster oeffnet.
            Centered(row.Missing > 0 ? row.Missing.ToString() : "-",
                row.Missing > 0 ? (row.Ignored ? Dim : Wanted) : Dim);

            // Preis: was der offene Haendler verlangt, sonst der Wert aus den Spieldaten
            ImGui.TableNextColumn();
            // Im Waehrungsladen ist der Preis in Scrips, nicht in Gil. Dann lieber
            // den Text aus den Spieldaten, der die Waehrung benennt.
            if (row.InShop && row.Price > 0)
                Priced(row.Currency != 0
                    ? $"{row.Price} {Restock.ItemName(row.Currency)}"
                    : $"{row.Price} Gil", null);
            else if (_vendors.PricesFor(row.BaitId) is { Count: > 0 } prices)
                PricedAll(prices, Dim);
            else if (_market.QuoteFor(row.BaitId) is { } quote)
            {
                // Vom Marktbrett, nicht aus den Spieldaten: von einem Spieler
                // gesetzt und in einer Stunde womoeglich ein anderer. Deshalb
                // gekennzeichnet und mit Alter im Hinweistext.
                Centered(quote.UnitPrice > 0 ? $"{quote.UnitPrice} Gil ~" : "none listed", Dim);
                if (ImGui.IsItemHovered())
                {
                    var age = DateTime.Now - quote.When;
                    var ago = age.TotalMinutes < 1
                        ? "just now"
                        : age.TotalHours < 1
                            ? $"{(int)age.TotalMinutes} min ago"
                            : $"{(int)age.TotalHours} h ago";
                    ImGui.SetTooltip(quote.UnitPrice > 0
                        ? $"Cheapest of {quote.Listings} market board listings, seen {ago}."
                        : $"Nothing was listed when checked {ago} — or the board did not answer.");
                }
            }
            else if (_market.UnlistedSince(row.BaitId) is { } when)
            {
                // Ueberdauert das Neuladen, anders als die Angebote selbst.
                Centered("none listed", Dim);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"Nothing was listed when checked {Ago(when)}." +
                                     Environment.NewLine +
                                     (_market.RecentlyUnlisted(row.BaitId)
                                         ? "It is left out of market board runs until that ages out."
                                         : "Old enough to try again on the next run."));
            }
            else
                Centered("—", Dim);

            ImGui.TableNextColumn();
            if (row.InShop)
            {
                if (row.Missing > 0 && !row.Ignored)
                {
                    if (ImGui.SmallButton("Buy"))
                        _queue.Start([row]);
                }
                else
                {
                    ImGui.TextDisabled("in shop");
                }
            }
            else
            {
                var vendors = _vendors.For(row.BaitId);
                if (vendors.Count > 0)
                {
                    ImGui.TextColored(Dim, vendors.Count == 1 ? "1 vendor" : $"{vendors.Count} vendors");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join(Environment.NewLine, vendors.Take(15).Select(v => $"{v} [{v.KindName}]")));

                    var best = vendors.FirstOrDefault(Reach.CanReach);
                    if (Reach.CanReach(best))
                    {
                        SameLineFarRight("Go");
                        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running || _market.Running || !_travel.Available))
                        {
                            if (ImGui.SmallButton("Go"))
                            {
                                // Cosmic Exploration hat keinen Aetheryten. Dorthin
                                // fuehrt nur der Fahrzeug-NPC, also der andere Weg.
                                if (CosmicTravel.PlanetOf(best.Zone) != null)
                                    _cosmic.StartTo(best);
                                else
                                    _travel.Start(best);
                            }
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(CosmicTravel.PlanetOf(best.Zone) != null
                                ? $"Ride to {best.Zone} and walk to {best.Npc} ({best.KindName})."
                                : $"Teleport to {best.AetheryteName} and walk to {best.Npc} ({best.KindName})."
                                             + (best.ApproximateHeight ? Environment.NewLine + "Height is estimated, the path may be off." : string.Empty));
                    }
                }
                else
                {
                    var other = _vendors.OtherSourcesFor(row.BaitId);
                    var recipe = _vendors.RecipeFor(row.BaitId);

                    // Ohne auffindbaren Haendler bleibt das Marktbrett. Ist die
                    // Option an, wird die Zeile anfahrbar statt nur beschriftet.
                    var board = _config.UseMarketBoard ? CachedBoard() : null;
                    if (board is { } destination)
                    {
                        ImGui.TextDisabled("market");
                        if (ImGui.IsItemHovered())
                        {
                            var lines = new List<string> { destination.ToString() };
                            if (recipe != null)
                                lines.Add($"Can also be crafted: {recipe}");
                            lines.AddRange(other.Take(8));
                            ImGui.SetTooltip(string.Join(Environment.NewLine, lines));
                        }

                        SameLineFarRight("Go");
                        using (ImRaiiDisabled(_travel.Running || _queue.Running || _sweep.Running
                                              || _market.Running || !_travel.Available))
                        {
                            if (ImGui.SmallButton("Go"))
                                _travel.Start(destination);
                        }
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip($"Teleport to {destination.AetheryteName} and walk to the market board.");
                    }
                    else if (other.Count > 0)
                    {
                        ImGui.TextDisabled(other.Count == 1 ? "1 source" : $"{other.Count} sources");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(string.Join(Environment.NewLine, other.Take(10)));
                    }
                    else if (recipe != null)
                    {
                        ImGui.TextDisabled("crafted");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(recipe);
                    }
                    else
                    {
                        ImGui.TextDisabled(_vendors.Ready ? "no source" : "…");
                        if (_vendors.Ready && ImGui.IsItemHovered())
                            ImGui.SetTooltip("No shop and no recipe. Gathered, fished or market board only.");
                    }
                }
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Woraus sich der Bestand zusammensetzt.
    ///
    /// Eine blanke Summe verschweigt das Entscheidende: Was in der
    /// Satteltasche liegt, holt man ueberall; was bei einem Gehilfen liegt, nur
    /// an der Rufglocke. Und was nie nachgesehen wurde, ist keine Null,
    /// sondern eine Unbekannte.
    /// </summary>
    private string StockBreakdown(Restock.Row row)
    {
        var lines = new List<string> { $"{row.Bag} in your bags" };

        // Abgeschaltet heisst: kommt hier nicht vor. Wer den Schalter umgelegt
        // hat, braucht keine Zeile, die ihn daran erinnert — die Gehilfen
        // halten es genauso.
        //
        // Eingeschaltet dagegen immer eine Aussage, auch wenn nichts drin
        // liegt: Ohne sie ist "160 in your bags" nicht davon zu unterscheiden,
        // dass die Tasche nie gezaehlt wurde.
        if (_config.CountSaddlebag)
        {
            if (!row.SaddleKnown)
                lines.Add("Saddlebag not counted — the game only hands out its contents "
                          + "once it has been opened.");
            else
                lines.Add(row.Saddle > 0
                    ? $"{row.Saddle} in the saddlebag"
                        + (row.SaddleSeen is { } when ? $" ({Ago(when)})" : string.Empty)
                    : "nothing in the saddlebag");
        }

        if (_config.CountRetainers)
        {
            var holdings = _retainers.Where(row.BaitId);
            foreach (var holding in holdings)
                lines.Add($"{holding.Count} with {holding.Name} ({Ago(holding.Seen)})");

            // Kennt das Spiel die Anzahl noch nicht, ist sie null — und "0 > 0"
            // ist falsch, also stand hier gar nichts. Genau dann ist der
            // Hinweis am noetigsten.
            var (total, seen) = _retainers.Coverage();
            if (total > 0 && seen < total)
                lines.Add($"{total - seen} of {total} retainers not counted yet — "
                          + "open them once at a summoning bell.");
            else if (total == 0 && seen == 0)
                lines.Add("No retainer counted yet — open one at a summoning bell "
                          + "and what it holds counts too.");
            else if (holdings.Count == 0)
                // Alle gezaehlt und nichts gefunden ist eine Auskunft. Sie
                // wegzulassen liesse offen, ob nachgesehen wurde — genau der
                // Grund, aus dem bei der Satteltasche "nothing" dasteht.
                lines.Add("nothing with your retainers");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
