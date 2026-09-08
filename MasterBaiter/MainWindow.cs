using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MasterBaiter;

internal sealed partial class MainWindow : Window
{
    private readonly Configuration _config;
    private readonly Restock _restock;
    private readonly PurchaseQueue _queue;
    private readonly ScripSweep _sweep;
    private readonly MarketBoard _market;
    private readonly CosmicTravel _cosmic;

    // Die Routenplanung laeuft ueber alle Koeder und alle ihre Haendler. Das
    // gehoert nicht in jedes Bild, nur weil der Knopf eine Zahl anzeigt.
    private List<Route.RouteStop> _plan = [];
    private long _planAt;

    // Dasselbe fuer das naechste Marktbrett: Die Suche geht ueber alle Bretter
    // und fuer jedes ueber alle Aetheryten. Einmal je Sekunde reicht.
    private VendorIndex.Vendor? _board;
    private long _boardAt;

    // Der Beutelinhalt geht ueber alle Zeilen und alle ihre Preise. Einmal je
    // Sekunde reicht, wie bei der Routenplanung.
    private List<Purse.Holding> _purse = [];
    private long _purseAt;

    // Beide Gruende gehen ueber alle Inventarfaecher. Je Bild waere das
    // dieselbe Rechnung sechzigmal je Sekunde, fuer einen Satz, der sich in
    // dieser Zeit nicht aendert.
    private string? _saddleFetch;
    private string? _saddleStow;
    private string? _retainerFetch;
    private string? _retainerStow;
    private long _blockersAt;
    private int _retainerRow;
    private readonly VendorIndex _vendors;
    private readonly RetainerStock _retainers;
    private readonly StashTransfer _stash;
    private readonly RetainerVisit _visit;
    private readonly RetainerRun _retainerRun;
    private readonly Travel _travel;
    private readonly Route _route;
    // Welche Zeile gerade ihr Ziel bearbeitet. 0 heisst: keine.
    private uint _editing;
    private bool _grabFocus;

    // Suchtext ueber der Tabelle. Mit "Show all fishing tackle" sind es 188
    // Zeilen; darin etwas zu finden, ohne zu suchen, ist Blaettern.
    private string _filter = string.Empty;

    // Die Vorschau wird im selben Frame geoeffnet, in dem der Knopf gedrueckt
    // wurde; ImGui verlangt OpenPopup und BeginPopup im gleichen ID-Bereich.
    private bool _openPreview;

    private string _message = string.Empty;
    private DateTime _messageUntil;
    private bool _wasRunning;

    public MainWindow(Configuration config, Restock restock, PurchaseQueue queue, ScripSweep sweep, MarketBoard market, CosmicTravel cosmic, VendorIndex vendors, Travel travel, Route route, RetainerStock retainers, StashTransfer stash, RetainerVisit visit, RetainerRun retainerRun)
        : base($"MasterBaiter {VersionText}###MasterBaiterMain")
    {
        _config = config;
        _restock = restock;
        _queue = queue;
        _sweep = sweep;
        _market = market;
        _cosmic = cosmic;
        _vendors = vendors;
        _retainers = retainers;
        _stash = stash;
        _visit = visit;
        _retainerRun = retainerRun;
        _travel = travel;
        _route = route;
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// Die Fassung fuer die Titelleiste. Aus der Assembly gelesen, nicht
    /// danebengeschrieben: Eine Zahl, die von Hand gepflegt wird, steht
    /// frueher oder spaeter falsch da. Die vierte Stelle ist immer null und
    /// faellt weg.
    /// </summary>
    private static string VersionText
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v == null ? string.Empty : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public override void OnOpen() => _restock.Refresh();

    public override void Draw()
    {
        // Bestaende jeden Frame nachziehen, damit die Tabelle waehrend eines
        // Kaufdurchlaufs mitlaeuft. Die Listendatei wird dabei nicht angefasst.
        _restock.RefreshCounts();
        _restock.AnnotateShop();

        // Nach dem Ende eines Durchlaufs einmal vollstaendig neu einlesen.
        var busy = _queue.Running || _sweep.Running || _market.Running;
        if (_wasRunning && !busy)
            _restock.Refresh();
        _wasRunning = busy;

        if (!ImGui.BeginTabBar("##masterbaiter"))
            return;

        if (ImGui.BeginTabItem("Baits"))
        {
            DrawBaitsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Options"))
        {
            DrawOptionsTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Debug"))
        {
            DrawDebugTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ---------- Reiter "Baits" ----------

    private static readonly Vector4 White = new(0.95f, 0.95f, 0.95f, 1f);
    private static readonly Vector4 Cyan = new(0.36f, 0.84f, 0.94f, 1f);
    private static readonly Vector4 Orange = new(1f, 0.62f, 0.25f, 1f);
    private static readonly Vector4 Purple = new(0.72f, 0.55f, 0.95f, 1f);
    private static readonly Vector4 Crimson = new(0.95f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Azure = new(0.45f, 0.65f, 0.98f, 1f);

    private void RefreshBlockers()
    {
        var now = Environment.TickCount64;
        if (now < _blockersAt)
            return;

        _blockersAt = now + 1000;
        _saddleFetch = _stash.Blocker(_restock.Rows, Stash.Saddlebag);
        _saddleStow = _stash.StowBlocker(_restock, Stash.Saddlebag);
        _retainerFetch = _stash.Blocker(_restock.Rows, Stash.Retainer);
        _retainerStow = _stash.StowBlocker(_restock, Stash.Retainer);
    }

    // ---------- Reiter "Options" ----------

    /// <summary>
    /// Bringt Scrip-Waehrungen auf ihr Kuerzel: "Purple Gatherers' Scrip" wird
    /// zu "Purple GS", "Purple Crafters' Scrip" zu "Purple CS".
    ///
    /// Ausgeschrieben passt keine Scrip-Zeile in eine vertretbare Preisspalte,
    /// und abgeschnitten steht dort "1 Purple Gath" — was nicht falsch ist,
    /// aber auch nicht lesbar. Die Farbe bleibt deshalb stehen, sie haelt die
    /// Tauschwaehrungen auseinander; nur der lange Rest wird zum Kuerzel.
    ///
    /// Absichtlich ein Muster statt einer Liste: Die Farben wechseln mit jeder
    /// Erweiterung, die Bauform "Farbe Rolle Scrip" nicht. Das Apostroph
    /// schreibt das Spiel mal als ' und mal als U+2019, beides muss passen.
    /// </summary>
    private static readonly Regex ScripName =
        new(@"\b(Gatherers|Crafters)['’]?s? Scrip\b", RegexOptions.Compiled);

    private static string Shorten(string text) =>
        ScripName.Replace(text, m => $"{m.Groups[1].Value[0]}S");

    /// <summary>
    /// Alle bekannten Preise eines Koeders: der erste in der Spalte, saemtliche
    /// im Hinweistext.
    ///
    /// Mancher Koeder ist in zwei Waehrungen zu haben — Dragonfly kostet
    /// Cosmocredits bei der Cosmic Exploration und Scrips am Tausch. In der
    /// Spalte steht nur einer davon, und welcher, haengt an der Fundreihenfolge.
    /// Wer wissen will, ob sich der andere Weg lohnt, sah das bisher nur in der
    /// Routenvorschau.
    ///
    /// Wo bekannt, steht dabei, wer die Waehrung nimmt: Ein Betrag ohne Laden
    /// beantwortet die halbe Frage.
    /// </summary>
    private static void PricedAll(IReadOnlyList<string> prices, Vector4? colour)
    {
        var shown = Shorten(prices[0]);
        var clipped = ImGui.CalcTextSize(shown).X > ImGui.GetContentRegionAvail().X;

        Centered(shown, colour);

        if (!ImGui.IsItemHovered())
            return;

        // Ein einzelner, ungekuerzter, vollstaendig sichtbarer Preis braucht
        // keinen Hinweistext, der ihn wiederholt.
        if (prices.Count == 1 && !clipped && shown == prices[0])
            return;

        ImGui.SetTooltip(string.Join(Environment.NewLine, prices.Select(WithVendor)));
    }

    /// <summary>Wer diese Waehrung annimmt, soweit es sich sagen laesst.</summary>
    private static string WithVendor(string price)
    {
        if (!PriceTag.TryParse(price, out var tag))
            return price;

        var where = tag.FitsVendor(VendorIndex.VendorKind.GilShop) ? "gil shop"
            : tag.FitsVendor(VendorIndex.VendorKind.ScripExchange) ? "scrip exchange"
            : tag.FitsVendor(VendorIndex.VendorKind.Cosmic) ? "Cosmic Exploration"
            : null;

        return where == null ? price : $"{price}  ({where})";
    }

    /// <summary>
    /// Preis mittig, und beim Ueberfahren noch einmal ganz.
    ///
    /// Der Hinweistext traegt nach, was die Spalte nicht zeigt: den
    /// ausgeschriebenen Waehrungsnamen ebenso wie einen trotzdem noch zu
    /// langen Text.
    /// </summary>
    private static void Priced(string text, Vector4? colour)
    {
        var shown = Shorten(text);
        var clipped = ImGui.CalcTextSize(shown).X > ImGui.GetContentRegionAvail().X;

        Centered(shown, colour);

        if ((clipped || shown != text) && ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;

        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";

        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>Sucht das naechste Marktbrett hoechstens einmal pro Sekunde.</summary>
    private VendorIndex.Vendor? CachedBoard()
    {
        var now = Environment.TickCount64;
        if (now < _boardAt)
            return _board;

        _boardAt = now + 1000;
        _board = MarketBoards.Nearest(_config, _vendors);
        return _board;
    }

    /// <summary>Plant hoechstens einmal pro Sekunde neu.</summary>
    private List<Route.RouteStop> CachedPlan()
    {
        var now = Environment.TickCount64;
        if (now < _planAt)
            return _plan;

        _planAt = now + 1000;
        _plan = _restock.Rows.Count > 0 && _vendors.Ready ? _route.Plan() : [];

        // Der Plan wird jede Sekunde neu gerechnet, aendert sich aber selten.
        // Als beobachteter Zustand steht er genau dann im Protokoll, wenn er
        // anders ausfaellt als vorher — und das ist die Frage, die man
        // hinterher stellt: Warum war dieser Halt gestern dabei und heute nicht?
        Trace.Change("Route plan", _plan.Count == 0
            ? "nothing to do"
            : string.Join(" | ", _plan.Select(s => $"{s.Vendor.Npc} ({s.Vendor.Zone}): " +
                                                   string.Join(", ", s.Baits))));

        return _plan;
    }

    private void Notify(string text)
    {
        _message = text;
        _messageUntil = DateTime.Now.AddSeconds(4);
    }

    /// <summary>Gedaempft — fuer alles, was gerade nichts von einem will.</summary>
    private static readonly Vector4 Dim = new(0.55f, 0.55f, 0.55f, 1f);

    /// <summary>
    /// Honiggelb. Die eine Akzentfarbe des Fensters — fuer die Fehlmenge, den
    /// aktiven Reiter, Haken und Regler. Mehr als eine Akzentfarbe waere keine.
    /// </summary>
    private static readonly Vector4 Honey = new(1f, 0.83f, 0.36f, 1f);

    /// <summary>
    /// Ein gesaettigtes Gelb, das man abdunkelt, ist Braun — daran ist nichts
    /// zu machen. Gelb bleibt nur gelb, solange es hell ist. Deshalb tragen die
    /// hellen Toene die Farbe, und die ruhenden Flaechen sind fast neutral:
    /// Braune Knoepfe und braune Reiter waren der ganze Fehler.
    /// </summary>
    private static readonly Vector4 HoneyLit = new(0.93f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 HoneyMid = new(0.74f, 0.58f, 0.20f, 1f);

    private static readonly Vector4 HoneyDark = new(0.20f, 0.18f, 0.14f, 0.90f);
    private static readonly Vector4 HoneyFaint = new(0.15f, 0.14f, 0.11f, 0.75f);

    /// <summary>Warm — fuer die Fehlmenge, die einzige Zahl, die zum Handeln auffordert.</summary>
    private static readonly Vector4 Wanted = Honey;

    /// <summary>
    /// Setzt den Knopf ans rechte Ende der Zelle statt direkt hinter den Text.
    ///
    /// Die Beschriftungen sind verschieden lang — "market" gegen "17 vendors" —,
    /// also stand jeder Knopf woanders und das Auge musste ihn in jeder Zeile
    /// neu suchen. Am rechten Rand stehen sie in einer Flucht und die Spalte
    /// wird zu einem Ziel statt zu zwanzig.
    /// </summary>
    private static void SameLineFarRight(string label)
    {
        var width = ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);

        ImGui.SameLine();
        var space = ImGui.GetContentRegionAvail().X;
        if (space > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + space - width);
    }

    /// <summary>
    /// Faerbt das Fenster honiggelb. Nur dieses Fenster und nur zwischen Pre-
    /// und PostDraw; die Farben des Spielers bleiben ueberall sonst, wie sie sind.
    /// </summary>
    private int _pushed;

    public override void PreDraw()
    {
        _pushed = 0;
        if (!_config.HoneyTheme)
            return;

        void Push(ImGuiCol which, Vector4 colour)
        {
            ImGui.PushStyleColor(which, colour);
            _pushed++;
        }

        // Ruhend: neutral dunkel. Beruehrt oder ausgewaehlt: wirklich gelb.
        Push(ImGuiCol.TitleBgActive, HoneyMid);
        Push(ImGuiCol.Tab, HoneyFaint);
        Push(ImGuiCol.TabHovered, HoneyLit);
        Push(ImGuiCol.TabActive, HoneyMid);
        Push(ImGuiCol.TabUnfocusedActive, HoneyDark);
        Push(ImGuiCol.Button, HoneyDark);
        Push(ImGuiCol.ButtonHovered, HoneyMid);
        Push(ImGuiCol.ButtonActive, HoneyLit);
        Push(ImGuiCol.Header, HoneyDark);
        Push(ImGuiCol.HeaderHovered, HoneyMid);
        Push(ImGuiCol.HeaderActive, HoneyLit);
        Push(ImGuiCol.FrameBg, HoneyFaint);
        Push(ImGuiCol.FrameBgHovered, HoneyDark);
        Push(ImGuiCol.FrameBgActive, HoneyMid);
        Push(ImGuiCol.CheckMark, Honey);
        Push(ImGuiCol.SliderGrab, HoneyMid);
        Push(ImGuiCol.SliderGrabActive, Honey);
        Push(ImGuiCol.TableHeaderBg, HoneyDark);
        Push(ImGuiCol.Separator, HoneyMid);
        Push(ImGuiCol.Border, HoneyMid);
        Push(ImGuiCol.ResizeGrip, HoneyDark);
        Push(ImGuiCol.ResizeGripHovered, HoneyMid);
        Push(ImGuiCol.ResizeGripActive, Honey);
    }

    public override void PostDraw()
    {
        if (_pushed > 0)
            ImGui.PopStyleColor(_pushed);

        _pushed = 0;
    }

    /// <summary>
    /// Die Kopfzeile von Hand, damit die Ueberschriften der Zahlenspalten
    /// mittig ueber ihren Zahlen stehen.
    ///
    /// <see cref="ImGui.TableHeadersRow"/> setzt jede Ueberschrift links an den
    /// Spaltenrand und kennt keine Ausrichtung. Bei mittigen Zahlen stand damit
    /// jede Ueberschrift neben statt ueber ihrer Spalte.
    /// </summary>
    private static void CenteredHeadersRow(int columns, int firstCentered, int lastCentered)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

        for (var i = 0; i < columns; i++)
        {
            if (!ImGui.TableSetColumnIndex(i))
                continue;

            var name = ImGui.TableGetColumnName(i).ToString();
            if (i >= firstCentered && i <= lastCentered)
            {
                var width = ImGui.CalcTextSize(name).X;
                var space = ImGui.GetContentRegionAvail().X;
                if (space > width)
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((space - width) * 0.5f));
            }

            ImGui.PushID(i);
            ImGui.TableHeader(name);
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Zahl mittig in der Spalte. Die Zahlenspalten sind schmal genug, dass die
    /// Ziffern damit unter ihrer Ueberschrift stehen — und alle vier stehen
    /// gleich, was vorher nicht der Fall war.
    /// </summary>
    private static void Centered(string text, Vector4? colour = null)
    {
        var width = ImGui.CalcTextSize(text).X;
        var space = ImGui.GetContentRegionAvail().X;
        if (space > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((space - width) * 0.5f));

        if (colour is { } c)
            ImGui.TextColored(c, text);
        else
            ImGui.TextUnformatted(text);
    }

    private static IDisposable ImRaiiDisabled(bool disabled) => new DisabledScope(disabled);

    private sealed class DisabledScope : IDisposable
    {
        private readonly bool _disabled;
        public DisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (_disabled) ImGui.BeginDisabled();
        }
        public void Dispose()
        {
            if (_disabled) ImGui.EndDisabled();
        }
    }
}
