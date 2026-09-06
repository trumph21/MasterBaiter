namespace MasterBaiter;

/// <summary>
/// Bewertet die Zeilen eines NPC-Auswahlmenues und sagt, welche in einen Laden
/// mit Koedern fuehrt.
///
/// Eigene Klasse, weil sie die einzige Stelle ist, an der geraten wird: Der
/// Menuetext sagt nicht zuverlaessig, was im Laden liegt. Beim Cosmocredit-
/// Tausch stehen die Koeder ausgerechnet unter "(Materials/Materia/Items)" —
/// eine Abwertung von "Materia" waere also falsch, so naheliegend sie klingt.
///
/// Ohne Spielzeiger und ohne Dalamud, damit sich die Regel pruefen laesst,
/// statt sie im Spiel auszuprobieren.
/// </summary>
internal static class MenuScoring
{
    /// <summary>Ein bewerteter Menueeintrag.</summary>
    internal readonly record struct Scored(int Index, string Text, int Score);

    /// <summary>
    /// Bewertet alle Zeilen, die ueberhaupt in einen Laden fuehren koennten.
    /// Zeilen ohne Kaufabsicht kommen gar nicht erst vor.
    /// </summary>
    public static List<Scored> Score(IReadOnlyList<string> entries)
    {
        var result = new List<Scored>();

        for (var i = 0; i < entries.Count; i++)
        {
            var text = entries[i].ToLowerInvariant();
            if (text.Length == 0)
                continue;

            // "Purchase Items" beim Kraemer, "Cosmocredit Exchange" im
            // Cosmic-Exploration-Gebiet — beides fuehrt in einen Warenladen.
            if (!text.Contains("purchase") && !text.Contains("buy") && !text.Contains("shop")
                && !text.Contains("exchange") && !text.Contains("trade"))
                continue;

            var score = 10;
            if (text.Contains("item")) score += 20;      // die Zeile mit den Waren
            if (text.Contains("bait")) score += 25;      // "… (Lv. 80 Materials/Bait/Tokens)"
            if (text.Contains("scrip")) score += 25;     // "Purchase items with … Scrips"
            if (text.Contains("tool")) score -= 15;
            if (text.Contains("token")) score -= 10;     // Token-Tausch, keine Koeder
            if (text.Contains("gear") || text.Contains("armor") || text.Contains("weapon")) score -= 15;

            result.Add(new Scored(i, entries[i], score));
        }

        return result;
    }

    /// <summary>
    /// Die beste Zeile, oder -1. Bei Gleichstand gewinnt die erste — das
    /// Auswahlmenue nennt die naheliegendste Wahl gewoehnlich zuerst.
    /// </summary>
    public static int Best(IReadOnlyList<string> entries, out string report)
    {
        var scored = Score(entries);
        report = string.Join(", ", scored.Select(s => $"{s.Text}={s.Score}"));

        var best = -1;
        var bestScore = int.MinValue;
        foreach (var s in scored)
        {
            if (s.Score <= bestScore)
                continue;
            bestScore = s.Score;
            best = s.Index;
        }

        return best;
    }
}
