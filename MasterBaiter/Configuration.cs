using Dalamud.Configuration;

namespace MasterBaiter;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Zielbestand fuer echte Koeder, wenn nichts eigenes hinterlegt ist.</summary>
    public int DefaultTarget { get; set; } = 300;

    /// <summary>
    /// Zielbestand fuer Kunstkoeder. Getrennt, weil sie ein Vielfaches kosten:
    /// 300 Wuermer sind ein paar tausend Gil, 300 Kunstkoeder ein Vermoegen.
    /// </summary>
    public int DefaultLureTarget { get; set; } = 10;

    /// <summary>Abweichender Zielbestand je Koeder-Item-Id.</summary>
    public Dictionary<uint, int> Targets { get; set; } = new();

    /// <summary>Nur Listen beruecksichtigen, die in GatherBuddy aktiviert sind.</summary>
    public bool OnlyEnabledLists { get; set; } = true;

    /// <summary>Pfad zu GatherBuddys auto_gather_lists.json. Leer = Standardpfad.</summary>
    public string GatherListPath { get; set; } = string.Empty;

    /// <summary>Koeder, die trotz Bedarf nicht nachgekauft werden sollen.</summary>
    public HashSet<uint> Ignored { get; set; } = [];

    /// <summary>Nach der Reise von allein kaufen, sobald der Laden offen ist.</summary>
    public bool BuyOnArrival { get; set; } = true;

    /// <summary>Koeder ohne Haendler am Marktbrett kaufen statt herzustellen.</summary>
    public bool UseMarketBoard { get; set; } = false;

    /// <summary>
    /// Hoechstpreis je Stueck. Ueber dieser Grenze wird ein Angebot nicht
    /// angefasst. Das ist keine Bequemlichkeit, sondern die Bremse: Am Marktbrett
    /// bestimmt kein Spieldatensatz den Preis, sondern der Anbieter.
    /// </summary>
    public int MarketMaxUnitPrice { get; set; } = 1000;

    /// <summary>Hoechstsumme je Durchlauf. Danach bricht der Lauf ab.</summary>
    public int MarketMaxGilPerRun { get; set; } = 100000;

    /// <summary>Ein Ort, den der Spieler selbst gesehen hat.</summary>
    [Serializable]
    internal sealed class Spot
    {
        public string Name { get; set; } = string.Empty;
        public uint Territory { get; set; }

        /// <summary>Fester Teleportpunkt. 0 heisst: den naechstgelegenen suchen.</summary>
        public uint AetheryteId { get; set; }
        public uint DataId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    /// <summary>Bretter, die die eingebaute Tabelle nicht kennt.</summary>
    public List<Spot> LearnedMarketBoards { get; set; } = [];

    /// <summary>
    /// Der NPC, der in die Cosmic Exploration schickt. Dorthin fuehrt kein
    /// Teleport, also wird sein Standort gemerkt, sobald der Spieler einmal
    /// mit ihm gesprochen hat.
    /// </summary>
    public Spot? CosmicGate { get; set; }

    /// <summary>
    /// Tempo aller automatischen Handlungen in Prozent. 100 ist der Grundwert,
    /// kleinere Zahlen sind schneller.
    /// </summary>
    public int PacingPercent { get; set; } = 100;

    /// <summary>
    /// Alle Angelkoeder des Spiels auflisten, nicht nur die, die deine Fische
    /// brauchen. Die zusaetzlichen stehen auf Zielmenge 0 — sonst wollte das
    /// Plugin sofort von hundertachtundzwanzig weiteren Koedern nachkaufen,
    /// darunter welche zu 99999 Gil das Stueck.
    /// </summary>
    public bool ShowAllTackle { get; set; }

    /// <summary>Kurze Rueckmeldung im Spielchat statt nur im Log.</summary>
    public bool ChatFeedback { get; set; } = true;

    /// <summary>Sprint einsetzen, solange das Plugin den Charakter bewegt.</summary>
    public bool UseSprint { get; set; }

    /// <summary>Zielplanet der Cosmic Exploration.</summary>
    public string CosmicPlanet { get; set; } = "Auxesia";

    /// <summary>
    /// Den Planeten selbst anklicken lassen statt ihn anzuklicken.
    ///
    /// Der Rueckruf des Planetenfensters ist nicht bekannt: Der Kennwert eines
    /// echten Klicks stammt aus dem Ereignis der Schaltflaeche, und derselbe
    /// Wert bedeutet als Rueckrufwert etwas anderes — er schliesst das Fenster.
    /// Solange das nicht geklaert ist, kann der Spieler die zwei Klicks selbst
    /// machen; der Rest der Kette laeuft weiter von allein.
    /// </summary>
    public bool AutoSelectPlanet { get; set; }

    public int TargetFor(uint baitId)
        => Targets.TryGetValue(baitId, out var v)
            ? v
            : Tackle.IsLure(baitId) ? DefaultLureTarget : DefaultTarget;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
