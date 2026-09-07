using System.Globalization;
using Lumina.Excel;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>
/// Ermittelt aus den Spieldaten, welcher Haendler welchen Koeder fuehrt, wo er
/// steht und ueber welchen Aetheryten man hinkommt.
///
/// Weg durch die Sheets:
///   GilShopItem      Unterzeile je Ladenposten, RowId ist der Laden
///   SpecialShop      dasselbe fuer andere Waehrungen
///   GCScripShopItem  Quartiermeister, haengt nicht an ENpcData
///   Recipe           was kein Laden fuehrt, stellt oft der Handwerker her
///   ENpcBase         ENpcData enthaelt die Laden-Ids eines NPCs
///   Level            Standort des NPCs in Weltkoordinaten
///   Aetheryte        Standorte der Aetheryten, fuer den naechstgelegenen
///
/// Kartenkoordinaten sind zum Anzeigen da, Weltkoordinaten zum Hinlaufen.
/// Beide werden mitgefuehrt.
/// </summary>
internal sealed class VendorIndex
{
    /// <summary>
    /// Art des Ladens, zugleich die Reihenfolge: Gil zuerst, dann Cosmic
    /// Exploration, dann der Scrip-Tausch. Gil ist beliebig nachschaffbar,
    /// Scrips sind es nicht — was mit Gil zu haben ist, soll auch mit Gil
    /// gekauft werden.
    /// </summary>
    public enum VendorKind
    {
        GilShop = 0,
        Cosmic = 1,
        ScripExchange = 2,
        Other = 3,
    }

    public readonly record struct Vendor(
        string Npc,
        string Zone,
        float MapX,
        float MapY,
        Vector3 World,
        uint Territory,
        uint AetheryteId,
        string AetheryteName,
        bool ApproximateHeight,
        uint DataId = 0,
        uint AethernetId = 0,
        VendorKind Kind = VendorKind.Other)
    {
        /// <summary>
        /// Der Scrip-Tausch in Idyllshire fuehrt die Koeder aller Erweiterungen
        /// an einem Ort. Sind fuer einen Koeder nur Scrip-Haendler im Angebot,
        /// spart dieser eine Halt die Fahrt zu mehreren Hubs.
        /// </summary>
        public bool PreferredScripHub
            => Kind == VendorKind.ScripExchange
               && Zone.Equals("Idyllshire", StringComparison.OrdinalIgnoreCase);

        public string KindName => Kind switch
        {
            VendorKind.GilShop => "gil",
            VendorKind.Cosmic => "cosmic exploration",
            VendorKind.ScripExchange => "scrip exchange",
            _ => "special shop",
        };

        /// <summary>Ist genug bekannt, um dorthin zu reisen?</summary>
        /// <summary>Reisen setzt Ziel und Teleportpunkt voraus.</summary>
        /// <summary>Ist der Standort bekannt? Sagt nichts darueber, wie man hinkommt.</summary>
        public bool HasPosition => Territory != 0 && World != Vector3.Zero;

        /// <summary>
        /// Standort UND Teleportpunkt bekannt.
        ///
        /// Nicht mit "erreichbar" verwechseln: Die Planeten der Cosmic
        /// Exploration haben keinen Aetheryten und sind trotzdem anfahrbar,
        /// naemlich ueber den Fahrzeug-NPC. Wer das hier abfragt, sperrt sie
        /// aus. Die Frage nach der Erreichbarkeit beantwortet <see cref="Reach"/>.
        /// </summary>
        public bool Navigable => HasPosition && AetheryteId != 0;

        // Feste Kultur, sonst wird aus "6.5, 9.6" bei deutscher Einstellung
        // "6,5, 9,6" und man sieht nicht mehr, wo die eine Koordinate endet.
        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "{0} — {1} ({2:0.0}, {3:0.0})", Npc, Zone, MapX, MapY);
    }

    /// <summary>ItemUICategory "Currency" — Scrips, Marken, Siegel. Keine Kristalle.</summary>
    private const uint CurrencyCategory = 100;

    private sealed record AetheryteSpot(uint Id, string Name, uint Territory, Vector3 World, bool IsShard, byte Group);

    private readonly Dictionary<uint, List<Vendor>> _byBait = new();
    private readonly Dictionary<uint, List<string>> _otherSources = new();
    private readonly Dictionary<uint, string> _recipes = new();
    /// <summary>
    /// Je Koeder alle bekannten Preise, nicht nur einen.
    ///
    /// Dragonfly kostet Cosmocredits bei der Cosmic Exploration und Scrips am
    /// Scrip-Tausch. Solange hier nur ein Preis stand, gewann der zuerst
    /// gefundene und der andere war verloren — an einem Halt, der ihn genommen
    /// haette, stand dann ein Fragezeichen. Die Reihenfolge ist die des
    /// Findens: Gil zuerst, danach die Sonderlaeden.
    /// </summary>
    private readonly Dictionary<uint, List<string>> _prices = new();

    private void AddPrice(uint itemId, string text)
    {
        lock (_lock)
        {
            if (!_prices.TryGetValue(itemId, out var list))
                _prices[itemId] = list = [];

            if (!list.Contains(text))
                list.Add(text);
        }
    }

    // Namen der Oberkategorien des Scrip-Tauschs, in denen ueberhaupt ein
    // Koeder liegt. Alles andere braucht der Durchlauf nicht anzufassen.
    private readonly HashSet<string> _scripCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly VendorFallback _fallback = new();
    private readonly PriceFallback _fallbackPrices = new();
    private readonly object _lock = new();

    private List<AetheryteSpot> _aetherytes = [];

    public bool Ready { get; private set; }
    public int VendorCount { get; private set; }

    /// <summary>
    /// Haendler mit bekanntem Standort. Kennen die Spieldaten keinen, greift die
    /// mitgelieferte Tabelle aus der Eorzea-Datenbank.
    /// </summary>
    public IReadOnlyList<Vendor> For(uint baitId)
    {
        lock (_lock)
        {
            if (_byBait.TryGetValue(baitId, out var v) && v.Count > 0)
                return v;
        }
        return _fallback.For(baitId);
    }

    /// <summary>
    /// Baut ein Reiseziel aus Gebiet und Weltposition: Zone, Kartenkoordinaten
    /// und der naechstgelegene Aetheryt werden nachgeschlagen.
    /// </summary>
    public Vendor MakeSpot(string name, uint territory, Vector3 world, uint dataId, uint fixedAetheryte = 0)
    {
        var (aetheryteId, aetheryteName, shardId) = NearestAetheryte(territory, world);

        // Ein fest gesetzter Teleportpunkt schlaegt die Suche. Sie taugt nur,
        // wo die Aetheryten eine Position haben — in Mare Lamentorum etwa hat
        // keiner der beiden eine, und dann gewinnt schlicht der erste.
        if (fixedAetheryte != 0)
        {
            aetheryteId = fixedAetheryte;
            aetheryteName = AetheryteName(fixedAetheryte) is { Length: > 0 } n ? n : aetheryteName;
            shardId = 0;
        }
        var map = Plugin.DataManager.GetExcelSheet<TerritoryType>()?
            .GetRowOrDefault(territory)?.Map.ValueNullable;

        var zone = map?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        var mapX = map == null ? 0f : ToMapCoordinate(world.X, map.Value.OffsetX, map.Value.SizeFactor);
        var mapY = map == null ? 0f : ToMapCoordinate(world.Z, map.Value.OffsetY, map.Value.SizeFactor);

        return new Vendor(name, zone, mapX, mapY, world, territory,
            aetheryteId, aetheryteName, false, dataId, shardId);
    }

    /// <summary>Der Name eines Aetheryten, leer wenn unbekannt.</summary>
    public string AetheryteName(uint id)
    {
        foreach (var spot in _aetherytes)
            if (spot.Id == id)
                return spot.Name;
        return string.Empty;
    }

    /// <summary>Wie weit ein Ziel vom zugehoerigen Aetheryten entfernt ist.</summary>
    public float DistanceToAetheryte(Vendor vendor)
    {
        foreach (var spot in _aetherytes)
            if (spot.Id == vendor.AetheryteId && spot.World != Vector3.Zero)
                return Vector3.Distance(spot.World, vendor.World);
        return 0f;
    }

    /// <summary>Bezugsquellen ohne Standort, etwa Quartiermeister.</summary>
    public IReadOnlyList<string> OtherSourcesFor(uint baitId)
    {
        lock (_lock)
            return _otherSources.TryGetValue(baitId, out var v) ? v : [];
    }

    /// <summary>
    /// Was der Koeder kostet, in Gil oder der jeweiligen Waehrung.
    ///
    /// Die Spieldaten gehen vor. Erst wenn sie nichts hergeben — bei
    /// Scrip-Koedern der Regelfall — kommt der mitgelieferte Wert aus der
    /// Eorzea-Datenbank zum Zug.
    /// </summary>
    public string? PriceFor(uint baitId)
    {
        lock (_lock)
            if (_prices.TryGetValue(baitId, out var known) && known.Count > 0)
                return known[0];

        return _fallbackPrices.For(baitId);
    }

    /// <summary>
    /// Alle bekannten Preise, damit sich der passende zum Laden aussuchen
    /// laesst. Der mitgelieferte Wert steht hinten: Spieldaten gehen vor.
    /// </summary>
    public List<string> PricesFor(uint baitId)
    {
        List<string> all;
        lock (_lock)
            all = _prices.TryGetValue(baitId, out var known) ? [.. known] : [];

        // Die mitgelieferten Preise hinten anhaengen, aber keinen doppelt: Was
        // die Spieldaten schon nennen, gilt von dort.
        foreach (var extra in _fallbackPrices.All(baitId))
            if (!all.Contains(extra))
                all.Add(extra);

        return all;
    }

    /// <summary>
    /// Fuehrt diese Oberkategorie des Scrip-Tauschs einen der Koeder? Ist die
    /// Liste leer, wurde nichts erkannt — dann lieber alles durchgehen als
    /// stillschweigend nichts zu kaufen.
    /// </summary>
    public bool ScripCategoryOfInterest(string name)
    {
        lock (_lock)
            return _scripCategories.Count == 0 || _scripCategories.Contains(name.Trim());
    }

    public int ScripCategoryCount
    {
        get
        {
            lock (_lock)
                return _scripCategories.Count;
        }
    }

    /// <summary>Handwerksrezept, falls es eines gibt.</summary>
    public string? RecipeFor(uint baitId)
    {
        lock (_lock)
            return _recipes.GetValueOrDefault(baitId);
    }

    private void AddOtherSource(uint baitId, string source)
    {
        lock (_lock)
        {
            if (!_otherSources.TryGetValue(baitId, out var list))
                _otherSources[baitId] = list = [];
            if (!list.Contains(source))
                list.Add(source);
        }
    }

    public void BuildAsync(IReadOnlyCollection<uint> baitIds)
        => Task.Run(() =>
        {
            try
            {
                Build(baitIds);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "[MasterBaiter] Could not build the vendor index.");
            }
        });

    private void Build(IReadOnlyCollection<uint> baitIds)
    {
        var wanted = new HashSet<uint>(baitIds);
        var started = Environment.TickCount64;

        _aetherytes = BuildAetherytes();
        _fallback.Resolve(FindZone, NearestAetheryte);

        var shopToBaits = new Dictionary<uint, List<uint>>();
        var shopKinds = new Dictionary<uint, VendorKind>();

        void Record(uint shopId, uint itemId, VendorKind kind)
        {
            if (!shopToBaits.TryGetValue(shopId, out var list))
                shopToBaits[shopId] = list = [];
            if (!list.Contains(itemId))
                list.Add(itemId);

            // Fuehrt ein Laden mehrere Arten, gewinnt die guenstigere.
            if (!shopKinds.TryGetValue(shopId, out var known) || kind < known)
                shopKinds[shopId] = kind;
        }

        // Cosmic Exploration zahlt mit eigener Waehrung. Der Name wird im
        // englischen Blatt geprueft, damit die Einstufung nicht an der
        // Spielsprache haengt.
        var englishItems = Plugin.DataManager.GetExcelSheet<Item>(Dalamud.Game.ClientLanguage.English);
        bool IsCosmicCurrency(uint itemId)
        {
            var name = englishItems?.GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
            return name.Contains("cosmo", StringComparison.OrdinalIgnoreCase)
                   || name.Contains("lunar credit", StringComparison.OrdinalIgnoreCase);
        }

        var gilShops = Plugin.DataManager.GetSubrowExcelSheet<GilShopItem>();
        if (gilShops != null)
            foreach (var row in gilShops.Flatten())
            {
                if (!wanted.Contains(row.Item.RowId))
                    continue;
                Record(row.RowId, row.Item.RowId, VendorKind.GilShop);

                // Der Ladenpreis eines Gil-Haendlers steht am Item selbst.
                var price = row.Item.ValueNullable?.PriceMid ?? 0;
                if (price > 0)
                    AddPrice(row.Item.RowId, $"{price} gil");
            }

        // SpecialShop-Zeile -> welche Koeder darin liegen. Der Scrip-Tausch
        // haengt nicht direkt am NPC, sondern verweist auf diese Zeilen.
        var specialShopBaits = new Dictionary<uint, List<uint>>();

        var specialShops = Plugin.DataManager.GetExcelSheet<SpecialShop>();
        if (specialShops != null)
            foreach (var shop in specialShops)
            foreach (var entry in shop.Item)
            foreach (var receive in entry.ReceiveItems)
            {
                var itemId = receive.Item.RowId;
                if (!wanted.Contains(itemId))
                    continue;
                var kind = VendorKind.Other;
                foreach (var cost in entry.ItemCosts)
                    if (cost.CurrencyCost > 0 && IsCosmicCurrency(cost.ItemCost.RowId))
                    {
                        kind = VendorKind.Cosmic;
                        break;
                    }

                Record(shop.RowId, itemId, kind);
                if (!specialShopBaits.TryGetValue(shop.RowId, out var inShop))
                    specialShopBaits[shop.RowId] = inShop = [];
                if (!inShop.Contains(itemId))
                    inShop.Add(itemId);
                AddOtherSource(itemId, shop.Name.ExtractText() is { Length: > 0 } n ? n : "Special shop");

                // Sonderlaeden nehmen andere Waehrungen. Gil hat Vorrang, falls
                // derselbe Koeder auch beim Kraemer liegt.
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.CurrencyCost == 0)
                        continue;

                    var costItem = cost.ItemCost.ValueNullable;

                    // Nur echte Waehrungen. Sonderlaeden tauschen auch Kristalle
                    // oder Materialien, und das ist kein Preis, den man wissen will.
                    //
                    // Cosmocredit und Lunar Credit stehen aber nicht in der
                    // Kategorie "Currency", sondern in "Other" — deshalb fiel
                    // jeder Preis der Cosmic Exploration durch diese Pruefung
                    // und die Vorschau zeigte dort ein Fragezeichen.
                    if (costItem == null)
                        continue;
                    if (costItem.Value.ItemUICategory.RowId != CurrencyCategory
                        && !IsCosmicCurrency(cost.ItemCost.RowId))
                        continue;

                    var currency = costItem.Value.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(currency))
                        continue;
                    var per = receive.ReceiveCount > 1 ? $" / {receive.ReceiveCount}" : string.Empty;
                    AddPrice(itemId, $"{cost.CurrencyCost} {currency}{per}");

                    // Mehrere Kosten in einem Eintrag zahlt man zusammen, nicht
                    // wahlweise. Der erste genuegt als Angabe.
                    break;
                }
            }

        // Scrip-Tausch. Der NPC fuehrt in seinen ENpcData keinen SpecialShop,
        // sondern eine InclusionShop-Zeile. Von dort geht es weiter:
        //   InclusionShop -> Category -> Series (Unterzeilen) -> SpecialShop
        // Ohne diesen Umweg taucht der Scrip-Haendler nirgends als Haendler auf,
        // und der Koeder sieht aus, als gaebe es ihn nur zum Herstellen.
        var inclusionShops = Plugin.DataManager.GetExcelSheet<InclusionShop>();
        if (inclusionShops != null && specialShopBaits.Count > 0)
            foreach (var shop in inclusionShops)
            foreach (var categoryRef in shop.Category)
            {
                var category = categoryRef.ValueNullable;
                if (category == null)
                    continue;

                var series = category.Value.InclusionShopSeries.ValueNullable;
                if (series == null)
                    continue;

                foreach (var entry in series.Value)
                {
                    var specialId = entry.SpecialShop.RowId;
                    if (!specialShopBaits.TryGetValue(specialId, out var baits))
                        continue;

                    // Der Name der Kategorie ist derselbe, den das Fenster spaeter
                    // in seiner oberen Auswahl zeigt. Darueber laesst sich der
                    // Durchlauf auf die paar Kategorien beschraenken, die zaehlen.
                    var categoryName = category.Value.Name.ExtractText().Trim();
                    if (categoryName.Length > 0)
                        lock (_lock)
                            _scripCategories.Add(categoryName);

                    foreach (var baitId in baits)
                    {
                        Record(shop.RowId, baitId, VendorKind.ScripExchange);
                        AddOtherSource(baitId, "Scrip exchange");
                    }
                }
            }

        var gcShops = Plugin.DataManager.GetSubrowExcelSheet<GCScripShopItem>();
        if (gcShops != null)
            foreach (var row in gcShops.Flatten())
                if (wanted.Contains(row.Item.RowId))
                    AddOtherSource(row.Item.RowId, "Grand Company quartermaster");

        var recipes = Plugin.DataManager.GetExcelSheet<Recipe>();
        if (recipes != null)
            foreach (var recipe in recipes)
            {
                var itemId = recipe.ItemResult.RowId;
                if (itemId == 0 || !wanted.Contains(itemId))
                    continue;

                var job = recipe.CraftType.ValueNullable?.Name.ExtractText() ?? "Crafting";
                var level = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
                var text = level > 0 ? $"{job} Lv. {level}" : job;
                if (recipe.AmountResult > 1)
                    text += $", yields {recipe.AmountResult}";

                lock (_lock)
                    _recipes.TryAdd(itemId, text);
            }

        if (shopToBaits.Count == 0)
        {
            Plugin.Log.Warning("[MasterBaiter] No vendor sells any of the tracked baits.");
            Ready = true;
            return;
        }

        // NPC -> Laden.
        //
        // Ein NPC fuehrt seinen Laden oft nicht unmittelbar. Dazwischen liegt
        // eines von zwei Blaettern:
        //
        //   TopicSelect  das Dialogmenue ("Purchase items", "Exchange scrips"),
        //                seine Shop-Spalte zeigt auf die echten Laeden
        //   PreHandler   kapselt einen Laden hinter einer Freischaltbedingung
        //                und verweist ueber Target weiter
        //
        // Scrip-Haendler stehen praktisch immer hinter einem TopicSelect. Wer
        // nur die direkten Eintraege prueft, findet sie nie und haelt ihre
        // Koeder fuer standortlos.
        // Voll qualifiziert: MasterBaiter.TopicSelect ist das Dialogfenster,
        // Lumina …Sheets.TopicSelect das Datenblatt dahinter.
        var topicSelects = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TopicSelect>();
        var preHandlers = Plugin.DataManager.GetExcelSheet<PreHandler>();

        // Sammelt alle Laeden, die hinter einem ENpcData-Eintrag stecken.
        void Resolve(uint id, List<uint> into, int depth)
        {
            if (depth > 3 || into.Contains(id))
                return;

            if (shopToBaits.ContainsKey(id))
            {
                into.Add(id);
                return;
            }

            if (topicSelects?.GetRowOrDefault(id) is { } topic)
                foreach (var shopRef in topic.Shop)
                    Resolve(shopRef.RowId, into, depth + 1);

            if (preHandlers?.GetRowOrDefault(id) is { } pre)
                Resolve(pre.Target.RowId, into, depth + 1);
        }

        var npcToShops = new Dictionary<uint, List<uint>>();
        var npcs = Plugin.DataManager.GetExcelSheet<ENpcBase>();
        var viaMenu = 0;
        if (npcs != null)
            foreach (var npc in npcs)
            foreach (var entry in npc.ENpcData)
            {
                var direct = shopToBaits.ContainsKey(entry.RowId);
                var found = new List<uint>();
                Resolve(entry.RowId, found, 0);
                if (found.Count == 0)
                    continue;

                if (!direct)
                    viaMenu++;

                if (!npcToShops.TryGetValue(npc.RowId, out var list))
                    npcToShops[npc.RowId] = list = [];
                foreach (var shopId in found)
                    if (!list.Contains(shopId))
                        list.Add(shopId);
            }

        // NPC -> Standort. Das Level-Sheet kennt laengst nicht alle NPCs;
        // neuere Gebiete platzieren sie ueber die Kartendateien.
        var residents = Plugin.DataManager.GetExcelSheet<ENpcResident>();
        var levels = Plugin.DataManager.GetExcelSheet<Level>();
        var result = new Dictionary<uint, List<Vendor>>();
        var located = new HashSet<uint>();

        if (levels != null)
            foreach (var level in levels)
            {
                var npcId = level.Object.RowId;
                if (!npcToShops.TryGetValue(npcId, out var shops))
                    continue;
                located.Add(npcId);

                var map = level.Map.ValueNullable;
                if (map == null)
                    continue;

                var name = residents?.GetRowOrDefault(npcId)?.Singular.ExtractText();
                name = string.IsNullOrWhiteSpace(name) ? $"NPC {npcId}" : Capitalise(name);

                var world = new Vector3(level.X, level.Y, level.Z);
                var territory = level.Territory.RowId;
                var (aetheryteId, aetheryteName, shardId) = NearestAetheryte(territory, world);
                var zone = map.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var mapX = ToMapCoordinate(level.X, map.Value.OffsetX, map.Value.SizeFactor);
                var mapY = ToMapCoordinate(level.Z, map.Value.OffsetY, map.Value.SizeFactor);

                foreach (var shop in shops)
                {
                    var kind = shopKinds.GetValueOrDefault(shop, VendorKind.Other);
                    var vendor = new Vendor(name, zone, mapX, mapY, world, territory,
                        aetheryteId, aetheryteName, false, npcId, shardId, kind);

                    foreach (var bait in shopToBaits[shop])
                    {
                        if (!result.TryGetValue(bait, out var list))
                            result[bait] = list = [];

                        // Denselben NPC nur einmal je Koeder, und zwar mit der
                        // guenstigsten Ladenart, die er dafuer anbietet.
                        var existing = list.FindIndex(v => v.DataId == npcId);
                        if (existing < 0)
                            list.Add(vendor);
                        else if (kind < list[existing].Kind)
                            list[existing] = vendor;
                    }
                }
            }

        var unlocated = 0;
        foreach (var (npcId, shops) in npcToShops)
        {
            if (located.Contains(npcId))
                continue;
            unlocated++;

            var name = residents?.GetRowOrDefault(npcId)?.Singular.ExtractText();
            name = string.IsNullOrWhiteSpace(name) ? $"NPC {npcId}" : Capitalise(name);

            foreach (var shop in shops)
            foreach (var bait in shopToBaits[shop])
                AddOtherSource(bait, $"{name} (location unknown)");
        }

        lock (_lock)
        {
            _byBait.Clear();
            foreach (var (bait, list) in result)
            {
                // Reihenfolge: Gil, dann Cosmic Exploration, dann Scrip-Tausch.
                // Danach die anfahrbaren zuerst, damit der Go-Knopf den nimmt.
                list.Sort((a, b) =>
                {
                    var byKind = a.Kind.CompareTo(b.Kind);
                    if (byKind != 0)
                        return byKind;

                    // Unter den Scrip-Haendlern zuerst Idyllshire.
                    var byHub = b.PreferredScripHub.CompareTo(a.PreferredScripHub);
                    if (byHub != 0)
                        return byHub;

                    var byReach = b.HasPosition.CompareTo(a.HasPosition);
                    return byReach != 0 ? byReach : string.Compare(a.Npc, b.Npc, StringComparison.CurrentCulture);
                });
                _byBait[bait] = list;
            }
        }

        var otherOnly = _otherSources.Keys.Count(k => !result.ContainsKey(k));
        VendorCount = result.Values.Sum(v => v.Count);
        Ready = true;
        Plugin.Log.Information(
            $"[MasterBaiter] Vendor index built in {Environment.TickCount64 - started} ms: " +
            $"{result.Count} baits with a located vendor, {VendorCount} entries, " +
            $"{otherOnly} only without coordinates, {_recipes.Count} craftable, " +
            $"{unlocated} NPCs without a location entry, " +
            $"{_fallback.BaitCount} baits from the shipped table, " +
            $"{_prices.Count} priced baits from the game data, " +
            $"{_fallbackPrices.Count} fallback prices, " +
            $"{_aetherytes.Count} aetherytes known, " +
            $"{result.Values.SelectMany(v => v).Count(v => v.PreferredScripHub)} entries at the " +
            $"preferred scrip hub, {_scripCategories.Count} scrip categories with bait, " +
            $"{viaMenu} shops found behind a dialog menu, " +
            $"{MarketBoards.BuiltInCount} market boards.");
    }

    // ---------- Aetheryten ----------

    /// <summary>
    /// Zum Teleportieren genuegen Id und Gebiet, beides steht im Aetheryte-Sheet.
    /// Die Position aus dem Level-Sheet ist Beiwerk: Sie entscheidet nur, welcher
    /// von mehreren Aetheryten desselben Gebiets der naechste ist. Fehlt sie,
    /// bleibt der Eintrag trotzdem brauchbar.
    /// </summary>
    private static List<AetheryteSpot> BuildAetherytes()
    {
        var list = new List<AetheryteSpot>();
        var sheet = Plugin.DataManager.GetExcelSheet<Aetheryte>();
        if (sheet == null)
            return list;

        // Positionen aus dem Level-Blatt vorziehen.
        //
        // Aetheryte.Level ist fast immer leer — von 452 Aetheryten hatten so
        // nur 28 eine Position. Die Verknuepfung steht in der Gegenrichtung:
        // Eine Level-Zeile zeigt auf den Aetheryten, nicht umgekehrt. Ohne
        // diese Suche ist "der naechstgelegene Aetheryt" eine Luege, und in
        // Limsa wird aus sechs Marktbrettern das erstbeste statt des naechsten.
        var positions = new Dictionary<uint, Vector3>();
        var levelSheet = Plugin.DataManager.GetExcelSheet<Level>();
        if (levelSheet != null)
            foreach (var level in levelSheet)
            {
                var objId = level.Object.RowId;
                if (objId == 0 || positions.ContainsKey(objId))
                    continue;
                positions[objId] = new Vector3(level.X, level.Y, level.Z);
            }

        var withPosition = 0;
        var shards = 0;
        foreach (var a in sheet)
        {
            if (a.RowId == 0)
                continue;

            var territory = a.Territory.RowId;
            if (territory == 0)
                continue;

            // Aethernet-Kristalle zaehlen mit. Manche Gebiete — The Firmament,
            // die Stadtviertel — haben ueberhaupt keinen eigenen Aetheryten,
            // und ohne die Kristalle kaeme man dort nie an.
            if (!a.IsAetheryte)
                shards++;

            var name = a.PlaceName.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                name = $"Aetheryte {a.RowId}";

            var position = Vector3.Zero;
            foreach (var levelRef in a.Level)
            {
                if (levelRef.ValueNullable is not { } level)
                    continue;
                position = new Vector3(level.X, level.Y, level.Z);
                break;
            }

            if (position == Vector3.Zero && positions.TryGetValue(a.RowId, out var found))
                position = found;

            if (position != Vector3.Zero)
                withPosition++;

            list.Add(new AetheryteSpot(a.RowId, name, territory, position, !a.IsAetheryte, a.AethernetGroup));
        }

        // Die Zuordnung steht nicht immer am Aetheryten: TerritoryType.Aetheryte
        // zeigt umgekehrt auf einen Aetheryten. Das ist aber selten die Zusage,
        // dass man dort ankommt — von 213 solchen Verweisen liegt der Aetheryt
        // in 207 Faellen in einem ANDEREN Gebiet. Fuer The Firmament etwa nennt
        // das Blatt Foundation, und ein Teleport dorthin laesst den Charakter
        // eine Zone vor dem Ziel stehen.
        //
        // Uebernommen wird der Verweis deshalb nur, wenn beide Gebiete denselben
        // Namen tragen. Das trifft die sechs Instanzvarianten — zweimal
        // "Ul'dah - Steps of Nald", "New Gridania", "Mor Dhona", "Lakeland",
        // "Ultima Thule" —, bei denen der Teleport tatsaechlich am Ziel landet.
        // Alles andere gilt als nicht anfliegbar, was ehrlicher ist als ein
        // Halt, der erst nach dem Teleport scheitert.
        var fromTerritory = 0;
        var skippedElsewhere = 0;
        var covered = new HashSet<uint>(list.Select(s => s.Territory));
        var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (territories != null)
            foreach (var tt in territories)
            {
                if (tt.RowId == 0 || covered.Contains(tt.RowId))
                    continue;

                var aetheryte = tt.Aetheryte.ValueNullable;
                if (aetheryte is not { RowId: > 0 })
                    continue;

                var here = tt.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                var lands = territories.GetRowOrDefault(aetheryte.Value.Territory.RowId)?
                    .PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (here.Length == 0 || !string.Equals(here, lands, StringComparison.OrdinalIgnoreCase))
                {
                    skippedElsewhere++;
                    continue;
                }

                var name = aetheryte.Value.PlaceName.ValueNullable?.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    name = $"Aetheryte {aetheryte.Value.RowId}";

                list.Add(new AetheryteSpot(aetheryte.Value.RowId, name, tt.RowId,
                    Vector3.Zero, !aetheryte.Value.IsAetheryte, aetheryte.Value.AethernetGroup));
                covered.Add(tt.RowId);
                fromTerritory++;
            }

        Plugin.Log.Information(
            $"[MasterBaiter] {list.Count} teleport points, {shards} aethernet shards, " +
            $"{withPosition} with a position, {fromTerritory} found via TerritoryType, " +
            $"{skippedElsewhere} skipped because the teleport lands in another zone.");
        return list;
    }

    /// <summary>Naechster Aetheryt im selben Gebiet, gemessen in der Ebene.</summary>
    public (uint Id, string Name, uint ShardId) NearestAetheryte(uint territory, Vector3 world)
    {
        if (territory == 0)
            return (0, string.Empty, 0);

        // Ein echter Aetheryt ist immer die erste Wahl, dorthin geht der Teleport
        // direkt. Ohne bekannte Position tut es der erste des Gebiets — in den
        // allermeisten Gebieten gibt es nur einen.
        var direct = (Id: 0u, Name: string.Empty);
        var best = (Id: 0u, Name: string.Empty, Distance: float.MaxValue);
        AetheryteSpot? shard = null;

        foreach (var a in _aetherytes)
        {
            if (a.Territory != territory)
                continue;

            if (a.IsShard)
            {
                shard ??= a;
                continue;
            }

            if (direct.Id == 0)
                direct = (a.Id, a.Name);

            if (a.World == Vector3.Zero || world == Vector3.Zero)
                continue;

            var dx = a.World.X - world.X;
            var dz = a.World.Z - world.Z;
            var d = dx * dx + dz * dz;
            if (d < best.Distance)
                best = (a.Id, a.Name, d);
        }

        if (best.Id != 0)
            return (best.Id, best.Name, 0);
        if (direct.Id != 0)
            return (direct.Id, direct.Name, 0);

        // Kein Aetheryt, nur ein Aethernet-Kristall: erst zum Stadt-Aetheryten
        // derselben Aethernet-Gruppe teleportieren, dann per Kristall weiter.
        if (shard == null)
            return (0, string.Empty, 0);

        foreach (var a in _aetherytes)
            if (!a.IsShard && a.Group == shard.Group)
                return (a.Id, $"{a.Name} → {shard.Name}", shard.Id);

        return (0, string.Empty, 0);
    }

    // ---------- Karten ----------

    /// <summary>Sucht Gebiet und Kartendaten anhand des angezeigten Gebietsnamens.</summary>
    public (uint Territory, short OffsetX, short OffsetY, ushort SizeFactor)? FindZone(string zoneName)
    {
        var maps = Plugin.DataManager.GetExcelSheet<Map>();
        if (maps == null || zoneName.Length == 0)
            return null;

        foreach (var map in maps)
        {
            var name = map.PlaceName.ValueNullable?.Name.ExtractText();
            if (!string.Equals(name, zoneName, StringComparison.OrdinalIgnoreCase))
                continue;
            var territory = map.TerritoryType.RowId;
            if (territory == 0)
                continue;
            return (territory, map.OffsetX, map.OffsetY, map.SizeFactor);
        }

        return null;
    }

    /// <summary>Weltkoordinate zu Kartenkoordinate, wie im Spiel angezeigt.</summary>
    private static float ToMapCoordinate(float world, short offset, ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        return (41f / scale * ((world + offset) * scale + 1024f) / 2048f) + 1f;
    }

    /// <summary>Umkehrung davon. Die Hoehe bleibt offen, die klaert vnavmesh.</summary>
    public static float ToWorldCoordinate(float map, short offset, ushort sizeFactor)
    {
        var scale = sizeFactor / 100f;
        return ((map - 1f) * 2048f * scale / 41f - 1024f) / scale - offset;
    }

    private static string Capitalise(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
