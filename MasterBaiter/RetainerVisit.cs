namespace MasterBaiter;

/// <summary>
/// Von der offenen Gehilfenliste bis zum offenen Gehilfeninventar.
///
/// Die Kette:
///
///     Zeile anwaehlen  ->  Sprechkasten  ->  Auswahlmenue  ->  Inventar
///
/// Das Auswahlmenue fehlte im Mitschnitt, und ich habe daraus geschlossen, es
/// gaebe keines. Falsch: Es meldet sich beim Anklicken nur nicht ueber den
/// Weg, den der Mitschnitt beobachtet. Im Spiel steht es da, mit einem Dutzend
/// Zeilen von "Entrust or withdraw items" bis "Quit".
///
/// Die Lehre ist dieselbe wie beim Raten, nur umgekehrt: Ein Mitschnitt zeigt,
/// was er sieht — nicht, was es gibt.
///
/// Der Sprechkasten will nur weitergeklickt werden. Ihn erneut anzusprechen
/// setzt ihn zurueck — dieselbe Falle wie bei den Haendlern, weshalb es hier
/// dieselbe Loesung gibt: <see cref="TalkDialog.Advance"/> statt eines zweiten
/// Anwaehlens.
/// </summary>
internal sealed class RetainerVisit(Configuration config)
{
    private enum Step { Idle, Selecting, Talking, Waiting, Done, Failed, Closing, Quitting, Farewell }

    /// <summary>So lange darf der ganze Vorgang dauern.</summary>
    private const int TimeoutMs = 20_000;

    private const int StepDelayMs = 500;

    /// <summary>
    /// So lange wird auf eine Reaktion gewartet, bevor die Zeile erneut
    /// angewaehlt wird.
    ///
    /// Der Klick kann verschluckt werden: Kommt die Liste gerade erst zurueck,
    /// nimmt sie ihn noch nicht an — meldet das aber nicht, sondern schweigt.
    /// Im Protokoll sah das so aus: angewaehlt, zwanzig Sekunden Stille, Frist
    /// abgelaufen. Ein zweiter Versuch nach drei Sekunden ist billiger.
    /// </summary>
    private const int NoReplyMs = 3000;

    private const int MaxSelects = 3;

    private Step _step = Step.Idle;
    private long _nextAt;
    private long _deadline;
    private int _index;
    private long _quietSince;
    private int _selects;

    public bool Running => _step is Step.Selecting or Step.Talking or Step.Waiting
        or Step.Closing or Step.Quitting or Step.Farewell;

    /// <summary>Wird ausgeloest, sobald wieder die Gehilfenliste offen ist.</summary>
    public event Action? Left;
    public string Status { get; private set; } = string.Empty;

    /// <summary>Wird ausgeloest, sobald das Inventar des Gehilfen offen ist.</summary>
    public event Action? Opened;

    public void Start(int index)
    {
        if (!RetainerList.IsOpen)
        {
            Fail("No retainer list open.");
            return;
        }

        _index = index;
        _selects = 0;
        _step = Step.Selecting;
        _nextAt = 0;
        _deadline = Environment.TickCount64 + TimeoutMs;
        Status = $"Opening retainer {index + 1}...";
    }

    /// <summary>
    /// Verlaesst den Gehilfen wieder.
    ///
    /// Spiegelbildlich zum Oeffnen, wie es das Spiel vorgibt:
    ///
    ///     Inventar schliessen  ->  Menue "Quit"  ->  Abschiedskasten  ->  Liste
    ///
    /// Ohne diesen Rueckweg bliebe die Kette beim ersten Gehilfen stehen, und
    /// der zweite waere nur ueber die Hand des Spielers erreichbar.
    /// </summary>
    public void Leave()
    {
        _step = Step.Closing;
        _nextAt = 0;
        _deadline = Environment.TickCount64 + TimeoutMs;
        Status = "Leaving the retainer...";
    }

    public void Stop(string reason)
    {
        _step = Step.Idle;
        Status = reason;
    }

    public void Tick()
    {
        if (!Running)
            return;

        var now = Environment.TickCount64;
        if (now > _deadline)
        {
            Fail($"Retainer {_index + 1} did not open in time (step {_step}).");
            return;
        }

        if (now < _nextAt)
            return;

        switch (_step)
        {
            case Step.Selecting:
                // Das Inventar kann schon offen sein, wenn jemand von Hand
                // vorgearbeitet hat.
                if (Stash.Retainer.Ready)
                {
                    Arrive();
                    return;
                }

                if (!RetainerList.Select(_index))
                {
                    Fail("The retainer list closed before a row could be picked.");
                    return;
                }

                _selects++;
                _quietSince = now;
                _step = Step.Talking;
                _nextAt = now + StepDelayMs;
                break;

            case Step.Talking:
                if (Stash.Retainer.Ready)
                {
                    Arrive();
                    return;
                }

                // Ein Sprechkasten will weitergeklickt werden, nicht erneut
                // angesprochen.
                if (TalkDialog.IsOpen)
                {
                    _quietSince = now;
                    TalkDialog.Advance();
                    _nextAt = now + StepDelayMs;
                    return;
                }

                if (ChooseItemMenu(now))
                    return;

                // Kein Kasten und noch kein Inventar: dem Spiel Zeit lassen.
                _step = Step.Waiting;
                _nextAt = now + StepDelayMs;
                break;

            case Step.Waiting:
                if (Stash.Retainer.Ready)
                {
                    Arrive();
                    return;
                }

                if (TalkDialog.IsOpen)
                {
                    _quietSince = now;
                    _step = Step.Talking;
                    return;
                }

                if (ChooseItemMenu(now))
                    return;

                // Seit dem Anwaehlen ist nichts passiert: nochmal klicken,
                // statt die Frist auszusitzen. Der Klick wird verschluckt,
                // wenn die Liste gerade erst zurueckgekommen ist — sie meldet
                // das aber nicht, sie schweigt einfach.
                if (now - _quietSince > NoReplyMs && RetainerList.IsOpen && _selects < MaxSelects)
                {
                    Plugin.Log.Information(
                        $"[MasterBaiter] Retainer {_index + 1} did not answer, selecting again " +
                        $"({_selects + 1} of {MaxSelects}).");
                    _step = Step.Selecting;
                    _nextAt = now + StepDelayMs;
                    return;
                }

                _nextAt = now + StepDelayMs;
                break;

            case Step.Closing:
                // Das Inventarfenster zuerst; darunter wartet das Menue.
                if (Stash.Retainer.Ready)
                {
                    CloseInventory();
                    _nextAt = now + StepDelayMs;
                    return;
                }

                _step = Step.Quitting;
                _nextAt = now + StepDelayMs;
                break;

            case Step.Quitting:
                if (RetainerList.IsOpen)
                {
                    Depart();
                    return;
                }

                if (TalkDialog.IsOpen)
                {
                    _step = Step.Farewell;
                    return;
                }

                if (TopicSelect.IsOpen)
                {
                    var entries = TopicSelect.Entries();
                    var quit = entries.FindIndex(e =>
                        e.StartsWith("Quit", StringComparison.OrdinalIgnoreCase));

                    if (quit < 0)
                    {
                        Fail("No \"Quit\" line in the retainer menu: " + string.Join(" | ", entries));
                        return;
                    }

                    TopicSelect.Select(quit);
                    _nextAt = now + StepDelayMs;
                    return;
                }

                _nextAt = now + StepDelayMs;
                break;

            case Step.Farewell:
                if (RetainerList.IsOpen)
                {
                    Depart();
                    return;
                }

                // Auch der Abschied ist ein Sprechkasten und will geklickt,
                // nicht angesprochen werden.
                if (TalkDialog.IsOpen)
                {
                    TalkDialog.Advance();
                    _nextAt = now + StepDelayMs;
                    return;
                }

                _step = Step.Quitting;
                _nextAt = now + StepDelayMs;
                break;
        }
    }

    /// <summary>Schliesst das Gehilfeninventar, welche der beiden Fassungen es auch ist.</summary>
    private static unsafe void CloseInventory()
    {
        foreach (var name in new[] { "InventoryRetainerLarge", "InventoryRetainer" })
        {
            var ptr = Plugin.GameGui.GetAddonByName(name);
            if (ptr.IsNull || !ptr.IsVisible)
                continue;

            ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address)->Close(true);
            return;
        }
    }

    private void Depart()
    {
        _step = Step.Done;
        Status = "Back at the retainer list.";
        Plugin.Log.Information($"[MasterBaiter] {Status}");
        Left?.Invoke();
    }

    /// <summary>
    /// Waehlt im Auswahlmenue die Zeile, die das Inventar oeffnet.
    ///
    /// Ueber den Text, nicht ueber eine Zeilennummer: Die Liste ist je nach
    /// Gehilfe verschieden lang — "Reset retainer class" steht nur bei manchen,
    /// "View venture report" nur bei laufender Unternehmung. Eine feste Zahl
    /// waere hier eine Wette.
    /// </summary>
    private bool ChooseItemMenu(long now)
    {
        if (!TopicSelect.IsOpen)
            return false;

        var entries = TopicSelect.Entries();
        var wanted = entries.FindIndex(e =>
            e.Contains("withdraw items", StringComparison.OrdinalIgnoreCase));

        if (wanted < 0)
        {
            Fail("No \"Entrust or withdraw items\" line in the retainer menu: "
                 + string.Join(" | ", entries));
            return true;
        }

        Plugin.Log.Information($"[MasterBaiter] Retainer menu: choosing \"{entries[wanted]}\".");
        TopicSelect.Select(wanted);
        _nextAt = now + StepDelayMs;
        _step = Step.Waiting;
        return true;
    }

    private void Arrive()
    {
        _step = Step.Done;
        Status = $"Retainer {_index + 1} is open.";
        Plugin.Log.Information($"[MasterBaiter] {Status}");
        Opened?.Invoke();
    }

    private void Fail(string reason)
    {
        _step = Step.Failed;
        Status = reason;
        Plugin.Log.Warning($"[MasterBaiter] Retainer: {reason}");
        ChatReport.Warn(config, reason);
    }
}
