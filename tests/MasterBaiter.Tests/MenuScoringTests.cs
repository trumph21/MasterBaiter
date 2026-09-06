using Xunit;

namespace MasterBaiter.Tests;

/// <summary>
/// Die Auswahl der Menuezeile. Die Faelle sind echte Dialoge aus dem Spiel —
/// einer davon hat mich schon einmal in die Materia-Abteilung geschickt.
/// </summary>
public class MenuScoringTests
{
    [Fact]
    public void Waehlt_beim_Kraemer_die_Warenzeile()
    {
        string[] entries = ["Purchase items.", "Small talk.", "Nothing."];

        Assert.Equal(0, MenuScoring.Best(entries, out _));
    }

    [Fact]
    public void Waehlt_am_Cosmocredit_Tausch_die_Zeile_mit_den_Koedern()
    {
        // Der Fall, der mich hereingelegt hat: Die Koeder liegen unter
        // "(Materials/Materia/Items)", nicht im schlichten Eintrag. Eine
        // Abwertung von "Materia" waere naheliegend und falsch.
        string[] entries =
        [
            "Cosmocredit Exchange",
            "Cosmocredit Exchange (Materials/Materia/Items)",
            "Cosmic Exploration Token Exchange",
            "Nothing",
        ];

        Assert.Equal(1, MenuScoring.Best(entries, out _));
    }

    [Fact]
    public void Zieht_am_Scrip_Tausch_die_Koederzeile_vor()
    {
        string[] entries =
        [
            "Purchase gear.",
            "Purchase items (Lv. 80 Materials/Bait/Tokens).",
            "Nothing.",
        ];

        Assert.Equal(1, MenuScoring.Best(entries, out _));
    }

    [Fact]
    public void Ignoriert_Zeilen_ohne_Kaufabsicht()
    {
        string[] entries = ["Small talk.", "Nothing.", "Ask about the weather."];

        Assert.Equal(-1, MenuScoring.Best(entries, out _));
    }

    [Fact]
    public void Wertet_Werkzeug_und_Ausruestung_ab()
    {
        string[] entries = ["Purchase fieldcraft tools.", "Purchase items."];

        Assert.Equal(1, MenuScoring.Best(entries, out _));
    }

    [Fact]
    public void Die_Bewertung_steht_im_Bericht()
    {
        // Der Bericht ist der Grund, warum eine Fehlwahl nachvollziehbar bleibt
        // — ohne ihn haette ich den Fall oben nur durch einen Dump gefunden.
        string[] entries = ["Scrip Exchange", "Small Talk", "Nothing"];

        MenuScoring.Best(entries, out var report);

        Assert.Contains("Scrip Exchange=35", report);
        Assert.DoesNotContain("Small Talk", report);
    }

    [Fact]
    public void Bei_Gleichstand_gewinnt_die_erste_Zeile()
    {
        string[] entries = ["Purchase items.", "Buy items."];

        Assert.Equal(0, MenuScoring.Best(entries, out _));
    }
}
