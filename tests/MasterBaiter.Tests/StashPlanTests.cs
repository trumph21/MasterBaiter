using MasterBaiter;
using Xunit;

namespace MasterBaiter.Tests;

public class StashPlanFetchTests
{
    private static StashPlan.Offer Stack(uint id, int amount) => new(id, amount);

    [Fact]
    public void StopsOnceTheGapIsCovered()
    {
        // Drei Stapel, aber zwoelf fehlen: Der erste deckt es, die anderen
        // waeren nur Ballast im Beutel.
        var offers = new[] { Stack(1, 50), Stack(2, 10), Stack(1, 30) };
        var shortBy = new Dictionary<uint, int> { [1] = 12 };

        Assert.Equal([0], StashPlan.Fetch(offers, shortBy, 10));
    }

    [Fact]
    public void TakesSeveralStacksWhenOneIsNotEnough()
    {
        var offers = new[] { Stack(1, 40), Stack(1, 40), Stack(1, 40) };
        var shortBy = new Dictionary<uint, int> { [1] = 90 };

        Assert.Equal([0, 1, 2], StashPlan.Fetch(offers, shortBy, 10));
    }

    [Fact]
    public void AStackLargerThanTheGapIsStillTaken()
    {
        // Der Fall aus dem Spiel: 288 Squid Strip im Beutel, 79 beim Gehilfen,
        // Ziel 300. Der Stapel passt nicht in die Luecke von zwoelf und kommt
        // trotzdem — er wird gebraucht und tut niemandem weh.
        var offers = new[] { Stack(1, 79) };
        var shortBy = new Dictionary<uint, int> { [1] = 12 };

        Assert.Equal([0], StashPlan.Fetch(offers, shortBy, 10));
    }

    [Fact]
    public void WhatIsNotShortIsLeftAlone()
    {
        var offers = new[] { Stack(1, 50), Stack(2, 50) };
        var shortBy = new Dictionary<uint, int> { [1] = 0 };

        Assert.Empty(StashPlan.Fetch(offers, shortBy, 10));
    }

    [Fact]
    public void NeverMoreThanThereIsRoomFor()
    {
        // Die Regel, die zwei Verbindungsabbrueche gekostet hat: Zuege in ein
        // volles Ziel sind Ablehnungen, und Ablehnungen sind Pakete.
        var offers = new[] { Stack(1, 10), Stack(1, 10), Stack(1, 10) };
        var shortBy = new Dictionary<uint, int> { [1] = 300 };

        Assert.Equal([0], StashPlan.Fetch(offers, shortBy, 1));
        Assert.Empty(StashPlan.Fetch(offers, shortBy, 0));
    }
}

public class StashPlanStowTests
{
    private static StashPlan.Offer Stack(uint id, int amount) => new(id, amount);

    private static readonly IReadOnlySet<uint> NothingNeeded = new HashSet<uint>();
    private static readonly IReadOnlyDictionary<uint, int> NoFloor = new Dictionary<uint, int>();

    [Fact]
    public void PutsAwayWhatNoFishNeeds()
    {
        var offers = new[] { Stack(1, 100) };
        var inBag = new Dictionary<uint, int> { [1] = 100 };

        Assert.Equal([0], StashPlan.Stow(offers, NothingNeeded, NoFloor, inBag, 10, out var kept));
        Assert.Equal(0, kept);
    }

    [Fact]
    public void LeavesWhatIsStillNeeded()
    {
        // Auch ein Ueberschuss bleibt: Wer dreihundert als Ziel hat und
        // vierhundert besitzt, will die hundert im Beutel behalten.
        var offers = new[] { Stack(1, 400) };
        var needed = new HashSet<uint> { 1 };
        var inBag = new Dictionary<uint, int> { [1] = 400 };

        Assert.Empty(StashPlan.Stow(offers, needed, NoFloor, inBag, 10, out _));
    }

    [Fact]
    public void KeepsAStackThatWouldDropYouUnderTheTarget()
    {
        // 504 in einem Stapel, ausdrueckliche Zielmenge 300: Wegraeumen hiesse,
        // mit null dazustehen, und teilen kann das Spiel nicht.
        var offers = new[] { Stack(1, 504) };
        var keep = new Dictionary<uint, int> { [1] = 300 };
        var inBag = new Dictionary<uint, int> { [1] = 504 };

        Assert.Empty(StashPlan.Stow(offers, NothingNeeded, keep, inBag, 10, out var kept));
        Assert.Equal(1, kept);
    }

    [Fact]
    public void TakesTheStackThatFitsAndKeepsTheRest()
    {
        // Dieselben 504, aber in zwei Stapeln: Der kleinere kann gehen, danach
        // liegen genau 300 im Beutel.
        var offers = new[] { Stack(1, 204), Stack(1, 300) };
        var keep = new Dictionary<uint, int> { [1] = 300 };
        var inBag = new Dictionary<uint, int> { [1] = 504 };

        Assert.Equal([0], StashPlan.Stow(offers, NothingNeeded, keep, inBag, 10, out var kept));
        Assert.Equal(1, kept);
    }

    [Fact]
    public void StacksOfTheSameBaitShareOneBalance()
    {
        // Vier Stapel zu je hundert, Untergrenze zweihundert: zwei duerfen weg,
        // die anderen beiden nicht. Wer den Bestand nicht mitfuehrt, raeumt
        // alle vier weg.
        var offers = new[] { Stack(1, 100), Stack(1, 100), Stack(1, 100), Stack(1, 100) };
        var keep = new Dictionary<uint, int> { [1] = 200 };
        var inBag = new Dictionary<uint, int> { [1] = 400 };

        Assert.Equal([0, 1], StashPlan.Stow(offers, NothingNeeded, keep, inBag, 10, out var kept));
        Assert.Equal(2, kept);
    }

    [Fact]
    public void NeverMoreThanThereIsRoomFor()
    {
        var offers = new[] { Stack(1, 10), Stack(2, 10), Stack(3, 10) };
        var inBag = new Dictionary<uint, int> { [1] = 10, [2] = 10, [3] = 10 };

        Assert.Equal([0, 1], StashPlan.Stow(offers, NothingNeeded, NoFloor, inBag, 2, out _));
        Assert.Empty(StashPlan.Stow(offers, NothingNeeded, NoFloor, inBag, 0, out _));
    }
}
