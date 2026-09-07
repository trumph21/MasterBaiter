using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>Rechnet aus, welche Koeder fehlen, und kauft sie am offenen Haendler nach.</summary>
internal sealed class Restock(Configuration config, BaitTable baits, GatherList list, RetainerStock retainers)
{
    public sealed class Row
    {
        public uint BaitId;
        public string Name = string.Empty;

        /// <summary>Was am Charakter haengt: Inventar, Ausruestung, Arsenal.</summary>
        public int Bag;

        /// <summary>
        /// Was in der Satteltasche liegt. Getrennt gefuehrt, weil ein Kauf
        /// dorthin nichts legt: Der Kaufdurchlauf muss auf den Beutel zielen,
        /// die Fehlmenge aber auf den Gesamtbestand.
        /// </summary>
        public int Saddle;

        /// <summary>
        /// Ist die Satteltasche ueberhaupt gelesen worden? Das Spiel gibt ihren
        /// Inhalt erst preis, nachdem sie einmal geoeffnet war. Vorher ist null
        /// keine Auskunft, sondern eine Vermutung.
        /// </summary>
        public bool SaddleKnown;

        /// <summary>Wann zuletzt in die Satteltasche gesehen wurde.</summary>
        public DateTime? SaddleSeen;

        /// <summary>
        /// Was bei den Gehilfen liegt, nach dem letzten Blick. Getrennt, weil
        /// es weder im Beutel liegt noch von einem Kauf dorthin wandert.
        /// </summary>
        public int Retainer;

        public int Have => Bag + Saddle + Retainer;
        public int Target;
        public int Missing => Math.Max(0, Target - Have);
        public List<uint> Fish = [];
        public bool Ignored;

        /// <summary>Braucht ein Fisch der Liste diesen Koeder, oder ist er nur mit aufgefuehrt?</summary>
        public bool Needed;

        // nur gesetzt, solange ein Haendlerfenster offen ist
        public int ShopIndex = -1;
        public uint Price;
        public uint Currency;
        public bool InShop => ShopIndex >= 0;
    }

    public List<Row> Rows { get; private set; } = [];
    public string? Status { get; private set; }

    public static unsafe int CountInInventory(uint itemId)
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(itemId);
    }

    /// <summary>
    /// Die Satteltasche, beide Haelften und beide Ausbaustufen.
    ///
    /// Ein Kauf legt dorthin nichts, aber wer 200 Mayfly darin liegen hat,
    /// braucht sie nicht ein zweites Mal. Ohne diese Zaehlung kauft das Plugin
    /// den Vorrat doppelt.
    /// </summary>
    private static readonly InventoryType[] Saddlebags =
    [
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    /// <summary>Wurde die Satteltasche jemals gelesen? Ueberdauert das Neuladen.</summary>
    public bool SaddlebagRead => config.SaddlebagSeen != null;

    /// <summary>Wann zuletzt hineingesehen wurde.</summary>
    public DateTime? SaddlebagSeen => config.SaddlebagSeen;

    private long _nextSaddleRead;

    /// <summary>
    /// Liest die Satteltasche, solange das Spiel sie hergibt.
    ///
    /// Gelesen wird nur, wenn die Beutel geladen sind — das Spiel laedt sie
    /// erst, wenn die Tasche einmal offen war, und gibt sie danach wieder frei.
    /// Deshalb wird der letzte gesehene Stand behalten: Sonst faellt der
    /// Bestand in dem Augenblick zurueck, in dem man das Fenster schliesst.
    ///
    /// Gehoert in den Framework-Takt.
    /// </summary>
    public unsafe void SaddlebagTick()
    {
        var now = Environment.TickCount64;
        if (now < _nextSaddleRead)
            return;
        _nextSaddleRead = now + 1000;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return;

        var seen = new Dictionary<uint, int>();
        var loaded = false;

        foreach (var type in Saddlebags)
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

                seen[slot->ItemId] = seen.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }

        // Nicht geladen heisst nicht leer. Den alten Stand stehen lassen.
        if (!loaded)
            return;

        // Nur schreiben, wenn sich etwas geaendert hat: Sonst ginge jede
        // Sekunde der Spielstand auf die Platte.
        if (config.SaddlebagSeen != null && Same(config.Saddlebag, seen))
            return;

        config.Saddlebag = seen;
        config.SaddlebagSeen = DateTime.Now;
        config.Save();
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

    /// <summary>
    /// Was davon in der Satteltasche liegt, nach dem letzten Blick.
    ///
    /// Ob ueberhaupt schon einmal hineingesehen wurde, beantwortet
    /// <see cref="SaddlebagRead"/> — bis dahin ist die Null keine Aussage und
    /// darf nicht als eine gelten.
    /// </summary>
    public int CountInSaddlebag(uint itemId) => config.Saddlebag.GetValueOrDefault(itemId);

    /// <summary>Die vier Beutel, die das Spiel als Inventar fuehrt.</summary>
    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>
    /// Wie viele Stueck davon noch ins Inventar passen.
    ///
    /// Nicht nur freie Felder: Ein angebrochener Stapel nimmt bis zur
    /// Stapelgroesse weiter auf, und Koeder stapeln sich bis 999. Arsenal und
    /// Sattelfach zaehlen nicht mit — dorthin legt ein Kauf nichts.
    ///
    /// Ein nicht geladener Beutel wird uebersprungen statt als voll gewertet:
    /// Lieber einen Kauf versuchen, der scheitern kann, als einen zu
    /// verweigern, der gegangen waere.
    /// </summary>
    public static unsafe int FreeSpaceFor(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;

        var stack = (int)(Plugin.DataManager.GetExcelSheet<Item>()?
            .GetRowOrDefault(itemId)?.StackSize ?? 999);
        if (stack <= 0)
            stack = 1;

        var space = 0;
        foreach (var type in Bags)
        {
            var bag = inv->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded)
                continue;

            for (var i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot == null)
                    continue;

                if (slot->ItemId == 0)
                    space += stack;
                else if (slot->ItemId == itemId)
                    space += Math.Max(0, stack - (int)slot->Quantity);
            }
        }

        return space;
    }

    public static string ItemName(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var row = sheet?.GetRowOrDefault(itemId);
        return row?.Name.ExtractText() ?? $"Item {itemId}";
    }

    public void Refresh()
    {
        var path = string.IsNullOrWhiteSpace(config.GatherListPath) ? GatherList.DefaultPath : config.GatherListPath;
        if (!list.Load(path))
        {
            Rows = [];
            Status = list.Error;
            return;
        }

        var needed = baits.BaitsFor(list.RelevantItemIds(config.OnlyEnabledLists));
        var rows = new List<Row>();
        foreach (var (baitId, fish) in needed)
        {
            rows.Add(new Row
            {
                BaitId = baitId,
                Name = ItemName(baitId),
                Bag = CountInInventory(baitId),
                Saddle = config.CountSaddlebag ? CountInSaddlebag(baitId) : 0,
                SaddleKnown = config.CountSaddlebag && SaddlebagRead,
                SaddleSeen = config.SaddlebagSeen,
                Retainer = config.CountRetainers ? retainers.CountFor(baitId) : 0,
                Target = config.TargetFor(baitId),
                Fish = fish,
                Ignored = config.Ignored.Contains(baitId),
                Needed = true,
            });
        }

        // Der Rest des Spiels, wenn gewuenscht. Ohne eigene Zielmenge bleiben
        // diese Zeilen bei 0 und werden nie gekauft — sie stehen nur da, damit
        // man ihnen eine geben kann.
        if (config.ShowAllTackle)
        {
            var known = new HashSet<uint>(rows.Select(r => r.BaitId));
            foreach (var baitId in Tackle.AllIds)
            {
                if (!known.Add(baitId))
                    continue;

                rows.Add(new Row
                {
                    BaitId = baitId,
                    Name = ItemName(baitId),
                    Bag = CountInInventory(baitId),
                Saddle = config.CountSaddlebag ? CountInSaddlebag(baitId) : 0,
                SaddleKnown = config.CountSaddlebag && SaddlebagRead,
                SaddleSeen = config.SaddlebagSeen,
                Retainer = config.CountRetainers ? retainers.CountFor(baitId) : 0,
                    Target = config.Targets.GetValueOrDefault(baitId),
                    Ignored = config.Ignored.Contains(baitId),
                    Needed = false,
                });
            }
        }

        // Was fehlt zuerst, dann die gebrauchten, dann der Rest.
        rows.Sort((a, b) =>
        {
            var byMissing = b.Missing.CompareTo(a.Missing);
            if (byMissing != 0)
                return byMissing;
            var byNeeded = b.Needed.CompareTo(a.Needed);
            return byNeeded != 0
                ? byNeeded
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        });

        Rows = rows;
        Status = null;
        AnnotateShop();
    }

    /// <summary>
    /// Zaehlt nur die Bestaende neu. Guenstig genug fuer jeden Frame, im
    /// Gegensatz zu <see cref="Refresh"/>, das die Listendatei neu einliest.
    /// </summary>
    public void RefreshCounts()
    {
        foreach (var r in Rows)
        {
            r.Bag = CountInInventory(r.BaitId);
            r.Saddle = config.CountSaddlebag ? CountInSaddlebag(r.BaitId) : 0;
            r.SaddleKnown = config.CountSaddlebag && SaddlebagRead;
            r.SaddleSeen = config.SaddlebagSeen;
            r.Retainer = config.CountRetainers ? retainers.CountFor(r.BaitId) : 0;
        }
    }

    /// <summary>Traegt Index und Preis nach, falls die Koeder im offenen Haendlerfenster stehen.</summary>
    public void AnnotateShop()
    {
        foreach (var r in Rows)
        {
            r.ShopIndex = -1;
            r.Price = 0;
            r.Currency = 0;
        }

        if (!ShopWindowReader.IsOpen)
            return;

        var entries = ShopWindowReader.ReadEntries();
        foreach (var r in Rows)
        {
            foreach (var e in entries)
            {
                if (e.ItemId != r.BaitId)
                    continue;
                r.ShopIndex = e.Index;
                r.Price = e.Price;
                r.Currency = e.CurrencyId;
                break;
            }
        }
    }
}
