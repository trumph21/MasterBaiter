using Xunit;

namespace MasterBaiter.Tests;

/// <summary>
/// Die Wartezeiten. Wenig spektakulaer, aber der Regler haengt daran: Ein
/// Vorzeichenfehler in der Skalierung wuerde das Plugin unbrauchbar langsam
/// oder gefaehrlich hastig machen.
/// </summary>
public class PacingTests
{
    [Fact]
    public void Bleibt_in_der_Naehe_des_Grundwerts()
    {
        Pacing.Percent = 100;

        for (var i = 0; i < 200; i++)
        {
            var d = Pacing.Delay(1000);
            Assert.InRange(d, 800, 1500);
        }
    }

    [Fact]
    public void Haelt_die_Untergrenze_ein()
    {
        // Sonst wuerde ein kleiner Prozentwert Abstaende von wenigen
        // Millisekunden erzeugen, und das Spiel verpasst Schritte.
        Pacing.Percent = 10;

        for (var i = 0; i < 200; i++)
            Assert.True(Pacing.Delay(100) >= 80);
    }

    [Fact]
    public void Der_Regler_wirkt_in_beide_Richtungen()
    {
        Pacing.Percent = 50;
        var fast = Mittel(1000);

        Pacing.Percent = 200;
        var slow = Mittel(1000);

        Pacing.Percent = 100;
        var normal = Mittel(1000);

        Assert.True(fast < normal, $"50% ({fast}) sollte unter 100% ({normal}) liegen");
        Assert.True(slow > normal, $"200% ({slow}) sollte ueber 100% ({normal}) liegen");
    }

    [Fact]
    public void Unsinnige_Werte_werden_begrenzt()
    {
        Pacing.Percent = 100000;
        var huge = Pacing.Delay(1000);

        Pacing.Percent = -50;
        var negative = Pacing.Delay(1000);

        Pacing.Percent = 100;

        Assert.InRange(huge, 1, 10000);
        Assert.True(negative >= 80);
    }

    private static double Mittel(int baseMs)
    {
        double sum = 0;
        for (var i = 0; i < 500; i++)
            sum += Pacing.Delay(baseMs);
        return sum / 500;
    }
}
