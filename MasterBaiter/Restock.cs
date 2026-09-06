using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>Rechnet aus, welche Koeder fehlen, und kauft sie am offenen Haendler nach.</summary>
internal sealed class Restock(Configuration config, BaitTable baits, GatherList list)
{
    public sealed class Row
    {
        public uint BaitId;
        public string Name = string.Empty;
        public int Have;
        public int Target;
        public int Missing => Math.Max(0, Target - Have);
        public List<uint> Fish = [];
        public bool Ignored;

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
                Have = CountInInventory(baitId),
                Target = config.TargetFor(baitId),
                Fish = fish,
                Ignored = config.Ignored.Contains(baitId),
            });
        }

        rows.Sort((a, b) => b.Missing.CompareTo(a.Missing) != 0
            ? b.Missing.CompareTo(a.Missing)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

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
            r.Have = CountInInventory(r.BaitId);
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

    /// <summary>Kauft alle fehlenden Koeder, die der offene Haendler fuehrt. Gibt die Anzahl der ausgeloesten Kaeufe zurueck.</summary>
    public int BuyMissing()
    {
        AnnotateShop();
        var bought = 0;
        foreach (var r in Rows)
        {
            if (r.Ignored || !r.InShop || r.Missing <= 0)
                continue;

            if (!ShopWindowReader.Buy(r.ShopIndex, r.Missing))
                continue;

            Plugin.Log.Information($"[MasterBaiter] {r.Missing}x {r.Name} gekauft (Index {r.ShopIndex}).");
            bought++;
        }

        if (bought > 0)
            Refresh();

        return bought;
    }
}
