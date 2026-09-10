using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MasterBaiter;

/// <summary>
/// Schreibt eine Auto-Gather-Liste in GatherBuddys Datei.
///
/// Das ist ein Eingriff in die Konfiguration eines fremden Plugins, und der
/// braucht Begruendung und Vorsicht in gleichem Mass.
///
/// <b>Warum die Datei und nicht die Schnittstelle:</b> GatherBuddys IPC hat
/// sechs Endpunkte — <c>Version</c>, <c>Identify</c>, <c>IsAutoGatherEnabled</c>,
/// <c>GetAutoGatherStatusText</c>, <c>SetAutoGatherEnabled</c>,
/// <c>IsAutoGatherWaiting</c>. Keiner davon fasst Listen an. Es gibt keinen
/// anderen Weg hinein.
///
/// <b>Was daran gefaehrlich ist:</b> GatherBuddy liest die Datei einmal beim
/// Laden und hat keinen Beobachter darauf. Sein <c>Save()</c> schreibt
/// saemtliche Listen aus dem Speicher zurueck und ueberschreibt dabei alles,
/// was inzwischen in der Datei steht. Ausgeloest wird es von jeder
/// Listenaenderung und von <c>SetActiveItems(removeCompletedItems)</c>, also
/// auch mitten im Sammeln.
///
/// Daraus folgen die Regeln hier:
///
///   * <b>Vorher eine Sicherung.</b> Sie liegt neben der Datei, mit Zeitstempel
///     im Namen, und wird nie geloescht.
///   * <b>Der Bestand wird durchgereicht, nicht neu gebaut.</b> Gelesen wird
///     als <see cref="JArray"/>, damit auch Felder erhalten bleiben, die dieses
///     Plugin nicht kennt. Eine Liste, die wir nicht angelegt haben, wird nicht
///     angefasst.
///   * <b>Zurueckgelesen und geprueft.</b> Eine Datei, die danach nicht mehr
///     zu lesen ist, waere der schlimmste Ausgang; sie wird sofort aus der
///     Sicherung wiederhergestellt.
///   * <b>Der Spieler wird zum Neuladen aufgefordert</b>, und zwar deutlich.
///     Vor dem Neuladen ist die Liste unsichtbar, und eine Aenderung in
///     GatherBuddy loescht sie stumm wieder.
/// </summary>
internal static class GatherListWriter
{
    /// <summary>
    /// Der Name der erzeugten Liste. Feststehend, damit ein zweiter Lauf sie
    /// ersetzt statt eine zweite danebenzulegen.
    /// </summary>
    public const string ListName = "Big Fish";

    /// <summary>Was beim Schreiben herauskam.</summary>
    internal readonly record struct Result(bool Ok, string Message, int Written, string? Backup);

    public static Result Write(IReadOnlyList<BigFish.Catch> fish, string path)
    {
        if (fish.Count == 0)
            return new Result(false, "Nothing to write: no big fish are missing from your log.", 0, null);

        if (!File.Exists(path))
            return new Result(false, $"GatherBuddy's list file is not there: {path}", 0, null);

        string original;
        JArray lists;
        try
        {
            // GatherBuddy schreibt teils mit BOM.
            original = File.ReadAllText(path);
            lists = JArray.Parse(original.TrimStart('﻿'));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MasterBaiter] Could not read GatherBuddy's list file.");
            return new Result(false, $"Could not read GatherBuddy's list file: {ex.Message}", 0, null);
        }

        // Sicherung zuerst. Ohne sie wird hier nichts geschrieben.
        string backup;
        try
        {
            backup = path + $".masterbaiter-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.WriteAllText(backup, original);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MasterBaiter] Could not write a backup, so nothing was changed.");
            return new Result(false, $"No backup could be written, so nothing was changed: {ex.Message}", 0, null);
        }

        var before = lists.Count;
        var order = 0;
        foreach (var entry in lists)
            order = Math.Max(order, entry.Value<int?>("Order") ?? 0);

        // Eine fruehere Fassung derselben Liste weicht; alles andere bleibt
        // Zeichen fuer Zeichen stehen.
        // Was vorher drinstand, wird notiert: Steht dort eine von Hand
        // gebaute Liste, ist der Unterschied die einzige Gelegenheit, die
        // eigene Auswahlregel gegen die zu pruefen, die GatherBuddys Filter
        // tatsaechlich liefern.
        var replaced = false;
        var previous = new HashSet<uint>();
        for (var i = lists.Count - 1; i >= 0; i--)
            if (lists[i].Value<string>("Name") == ListName)
            {
                foreach (var id in lists[i]["ItemIds"]?.Values<uint>() ?? [])
                    previous.Add(id);

                lists.RemoveAt(i);
                replaced = true;
            }

        lists.Add(Build(fish, order + 1));

        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(lists, Formatting.Indented));

            // Zurueckgelesen: Eine Datei, die GatherBuddy nicht mehr laden kann,
            // waere schlimmer als gar keine Liste.
            var check = JArray.Parse(File.ReadAllText(path).TrimStart('﻿'));
            if (check.Count != before + (replaced ? 0 : 1))
                throw new InvalidOperationException(
                    $"the file now holds {check.Count} lists, expected {before + (replaced ? 0 : 1)}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[MasterBaiter] Writing the list failed, restoring the backup.");
            try { File.WriteAllText(path, original); }
            catch (Exception restore)
            {
                Plugin.Log.Error(restore, "[MasterBaiter] The backup could not be restored either.");
                return new Result(false,
                    $"Writing failed AND the restore failed. Your file is at: {backup}", 0, backup);
            }

            return new Result(false, $"Writing failed, the old file is back: {ex.Message}", 0, backup);
        }

        if (replaced)
            Compare(previous, fish);

        Plugin.Log.Information(
            $"[MasterBaiter] Wrote \"{ListName}\" with {fish.Count} fish to {path} " +
            $"({(replaced ? "replaced the earlier one" : "new list")}, backup at {Path.GetFileName(backup)}). " +
            "GatherBuddy must be reloaded before it sees this.");

        return new Result(true,
            $"{fish.Count} fish written. Reload GatherBuddy to see the list.", fish.Count, backup);
    }

    /// <summary>
    /// Schreibt auf, worin sich die neue Liste von der ersetzten unterscheidet.
    ///
    /// Die Auswahlregel hier ist aus GatherBuddys Quelltext abgeleitet, aber
    /// nie gegen dessen laufende Tabelle gehalten worden. Wer die Liste einmal
    /// von Hand gebaut hat, liefert damit die Gegenprobe — und ein einzelner
    /// abweichender Fisch sagt mehr als eine uebereinstimmende Gesamtzahl.
    /// </summary>
    private static void Compare(HashSet<uint> previous, IReadOnlyList<BigFish.Catch> fish)
    {
        var mine = fish.Select(f => f.ItemId).ToHashSet();
        var added = mine.Except(previous).ToList();
        var gone = previous.Except(mine).ToList();

        Plugin.Log.Information(
            $"[MasterBaiter] Against the \"{ListName}\" that was there: {previous.Count} before, " +
            $"{mine.Count} now, {added.Count} added, {gone.Count} dropped.");

        if (added.Count > 0)
            Plugin.Log.Information("[MasterBaiter]   Only in mine: "
                + string.Join(", ", added.Take(40).Select(Restock.ItemName)));

        if (gone.Count > 0)
            Plugin.Log.Information("[MasterBaiter]   Only in yours: "
                + string.Join(", ", gone.Take(40).Select(Restock.ItemName)));
    }

    /// <summary>
    /// Eine Liste in genau der Form, die GatherBuddy schreibt.
    ///
    /// Auch <c>PrefferedLocations</c> mit seinem Schreibfehler — das Feld heisst
    /// dort so, und ein korrigierter Name waere ein unbekanntes Feld.
    ///
    /// <c>Enabled</c> ist <c>true</c>, <c>RemoveCompletedItems</c> auch,
    /// <c>Fallback</c> nicht — der Zustand, den der Spieler von Hand herstellt.
    /// Ich hatte sie zuerst ausgeschaltet ausgeliefert, aus Sorge vor der
    /// Koederrechnung fuer hundertdreissig Fische; das ist seine Entscheidung
    /// und nicht meine. Ein Knopf, nach dem noch ein Handgriff noetig ist, hat
    /// seine Aufgabe nur halb erledigt.
    /// </summary>
    private static JObject Build(IReadOnlyList<BigFish.Catch> fish, int order)
    {
        var ids = new JArray();
        var quantities = new JObject();
        var enabled = new JObject();

        foreach (var entry in fish)
        {
            ids.Add(entry.ItemId);
            quantities[entry.ItemId.ToString()] = 1;
            enabled[entry.ItemId.ToString()] = true;
        }

        return new JObject
        {
            ["ItemIds"] = ids,
            ["Quantities"] = quantities,
            ["PrefferedLocations"] = new JObject(),
            ["EnabledItems"] = enabled,
            ["Name"] = ListName,
            ["Description"] = "Big fish missing from your fishing log, written by MasterBaiter on "
                              + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".",
            ["FolderPath"] = string.Empty,
            ["Order"] = order,
            ["Enabled"] = true,
            ["Fallback"] = false,
            ["RemoveCompletedItems"] = true,
        };
    }
}
