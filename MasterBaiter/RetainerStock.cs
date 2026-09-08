using FFXIVClientStructs.FFXIV.Client.Game;

namespace MasterBaiter;

/// <summary>
/// Was bei den Gehilfen liegt.
///
/// Anders als Inventar und Satteltasche haelt das Spiel die Beutel der Gehilfen
/// nicht bereit: Geladen ist immer nur der eine, mit dem man gerade spricht.
/// Wer alle zaehlen will, muss sich merken, was er beim letzten Blick gesehen
/// hat — und dazu sagen, wann das war.
///
/// Deshalb steht in der Anzeige nie eine blanke Zahl, sondern immer, von wann
/// sie ist und welcher Gehilfe noch nie geoeffnet wurde. Ein Vorrat, den man
/// vor drei Tagen gesehen hat, ist eine Erinnerung, keine Auskunft.
/// </summary>
internal sealed class RetainerStock(Configuration config)
{
    /// <summary>Die Beutelseiten eines Gehilfen. Kristalle und Marktware bleiben aussen vor.</summary>
    private static readonly InventoryType[] Pages =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2,
        InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>Ein Gehilfe mit dem, was zuletzt bei ihm zu sehen war.</summary>
    public sealed record Holding(string Name, int Count, DateTime Seen);

    private readonly HashSet<uint> _tracked = [];

    /// <summary>Welche Koeder ueberhaupt mitgeschrieben werden.</summary>
    public void Track(IEnumerable<uint> baitIds)
    {
        _tracked.Clear();
        foreach (var id in baitIds)
            _tracked.Add(id);
    }

    /// <summary>Wie viele Gehilfen der Charakter hat, und wie viele davon gesehen wurden.</summary>
    public unsafe (int Total, int Seen) Coverage()
    {
        var rm = RetainerManager.Instance();
        var total = rm == null ? 0 : (int)rm->GetRetainerCount();

        // Vor dem ersten Gang zur Rufglocke gibt das Spiel die Liste nicht
        // heraus. Null ist dann keine Aussage ueber die Anzahl, sondern das
        // Eingestaendnis, sie nicht zu kennen — was notiert wurde, gilt aber.
        if (total <= 0)
            return (0, config.Retainers.Count);

        // Nur die, an die man auch herankommt.
        //
        // Wer sein Abonnement verkleinert, behaelt seine Gehilfen, kann sie
        // aber nicht mehr oeffnen — im Spiel stehen sie ausgegraut. Sie in die
        // Anzahl zu nehmen hiesse, eine Aufforderung stehen zu lassen, der
        // niemand nachkommen kann.
        var reachable = 0;
        var seen = 0;

        for (var i = 0; i < total; i++)
        {
            var r = rm->GetRetainerBySortedIndex((uint)i);
            if (r == null || !r->Available)
                continue;

            reachable++;
            if (config.Retainers.ContainsKey(r->RetainerId))
                seen++;
        }

        return (reachable, seen);
    }

    /// <summary>
    /// Schreibt mit, was beim offenen Gehilfen liegt.
    ///
    /// Gehoert in den Framework-Takt. Ist keiner offen, geschieht nichts — der
    /// zuletzt gemerkte Stand bleibt stehen, mitsamt seinem Datum.
    /// </summary>
    public unsafe void Tick()
    {
        // Erst wenn das Beutelfenster wirklich offen ist.
        //
        // Der aktive Gehilfe wechselt beim Anwaehlen der Zeile, seine Taschen
        // ein bis zwei Sekunden spaeter. Dazwischen meldet das Spiel den neuen
        // Namen ueber dem Beutel des alten — im Protokoll stand deshalb einmal
        // "Noted 27 bait type(s) with Notacloneofmeone", und die 27 gehoerten
        // dem Gehilfen davor. Es hat sich nach 474 ms selbst berichtigt, weil
        // dieser Gang lange genug dauerte; ein schnellerer haette den fremden
        // Bestand im Spielstand stehen lassen. Ein zu hoher Bestand heisst
        // Fehlmenge zu klein, und dann wird nicht gekauft, was fehlt.
        //
        // <c>IsLoaded</c> allein reicht dafuer nicht — geschlossene Faecher
        // melden es ebenfalls, was diesem Plugin schon dreimal begegnet ist.
        // Das offene Fenster ist der Beleg, nicht die Kennung dahinter.
        if (!Stash.Retainer.Ready)
            return;

        var rm = RetainerManager.Instance();
        if (rm == null)
            return;

        var active = rm->GetActiveRetainer();
        if (active == null || active->RetainerId == 0)
            return;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return;

        var counts = new Dictionary<uint, int>();
        var loaded = false;

        foreach (var type in Pages)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            loaded = true;

            for (var i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;
                if (!_tracked.Contains(slot->ItemId))
                    continue;

                counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }

        // Keine geladene Seite heisst: Das Fenster ist noch im Aufbau. Jetzt
        // etwas zu merken hiesse, einen leeren Beutel zu behaupten.
        if (!loaded)
            return;

        var name = active->NameString;
        var known = config.Retainers.GetValueOrDefault(active->RetainerId);

        // Nur schreiben, wenn sich etwas geaendert hat. Sonst schriebe jeder
        // Frame die Konfiguration auf die Platte.
        if (known != null && known.Name == name && Same(known.Items, counts))
            return;

        config.Retainers[active->RetainerId] = new Configuration.RetainerNote
        {
            Name = name,
            Seen = DateTime.Now,
            Items = counts,
        };
        config.Save();

        Plugin.Log.Information(
            $"[MasterBaiter] Noted {counts.Count} tracked bait type(s) with {name}.");
    }

    private static bool Same(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count)
            return false;

        foreach (var (id, n) in a)
            if (!b.TryGetValue(id, out var m) || m != n)
                return false;

        return true;
    }

    /// <summary>Wo dieser Koeder liegt, und wie viel.</summary>
    public List<Holding> Where(uint baitId)
    {
        var found = new List<Holding>();

        foreach (var note in config.Retainers.Values)
            if (note.Items.TryGetValue(baitId, out var count) && count > 0)
                found.Add(new Holding(note.Name, count, note.Seen));

        return found.OrderByDescending(h => h.Count).ToList();
    }

    public int CountFor(uint baitId)
    {
        var total = 0;
        foreach (var note in config.Retainers.Values)
            total += note.Items.GetValueOrDefault(baitId);

        return total;
    }
}
