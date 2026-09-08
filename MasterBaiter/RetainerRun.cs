using FFXIVClientStructs.FFXIV.Client.Game;

namespace MasterBaiter;

/// <summary>
/// Der ganze Gang durch alle Lager: erst die Satteltasche, dann die Gehilfen.
///
///     Satteltasche  ->  holen  ->  wegraeumen
///                                      |
///     Glocke  ->  Gehilfe oeffnen  ->  holen  ->  wegraeumen  ->  verlassen
///                      ^                                             |
///                      +---------------- naechster --------------- +
///
/// Die Satteltasche zuerst, weil sie ueberall aufgeht und keine Reise kostet:
/// Was von dort in den Beutel wandert, muss bei den Gehilfen nicht mehr geholt
/// werden.
///
/// Neue Mechanik steckt hier keine. Jeder einzelne Schritt ist gebaut und
/// bestaetigt worden — diese Klasse haelt nur fest, in welcher Reihenfolge sie
/// laufen und woran man merkt, dass einer fertig ist.
///
/// Uebersprungen wird, wer sich nicht oeffnen laesst: Nach dem Verkleinern
/// eines Abonnements bleiben Gehilfen in der Liste stehen, die niemand mehr
/// ansprechen kann. Sie behalten dabei ihre Zeile, deshalb wird ueber die
/// Zeilennummern gegangen und nicht ueber eine fortlaufende Zaehlung — sonst
/// verrutscht alles dahinter.
/// </summary>
internal sealed unsafe class RetainerRun(
    Configuration config,
    Restock restock,
    VendorIndex vendors,
    Travel travel,
    RetainerVisit visit,
    StashTransfer stash)
{
    private enum Step
    {
        Idle,
        BagFetch, BagStow,
        Travelling, Opening, Fetching, Stowing, Leaving,
        Done, Failed,
    }

    /// <summary>Notbremse fuer den ganzen Gang.</summary>
    private const int TimeoutMs = 300_000;

    private const int StepDelayMs = 700;

    /// <summary>
    /// Pause zwischen zwei Gehilfen.
    ///
    /// Siebenhundert Millisekunden nach der Rueckkehr zur Liste waren zu wenig:
    /// Die Auswahl des zweiten Gehilfen wurde verschluckt.
    /// </summary>
    private const int BetweenRetainersMs = 2000;

    private readonly List<int> _rows = [];
    private int _at;
    private Step _step = Step.Idle;
    private long _nextAt;
    private long _deadline;
    private int _visited;
    private bool _startedTravel;
    private int _fetched;
    private int _stowed;
    private bool _sawSaddlebag;

    public bool Running => _step is not (Step.Idle or Step.Done or Step.Failed);
    public string Status { get; private set; } = string.Empty;

    /// <summary>Die Zeilen der Gehilfen, die sich oeffnen lassen.</summary>
    private static List<int> OpenableRows()
    {
        var rows = new List<int>();
        var manager = RetainerManager.Instance();
        if (manager == null)
            return rows;

        var count = (int)manager->GetRetainerCount();
        for (var i = 0; i < count; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex((uint)i);
            if (retainer != null && retainer->Available)
                rows.Add(i);
        }

        return rows;
    }

    public string? Blocker()
    {
        if (!travel.Available)
            return "vnavmesh or Lifestream is missing.";

        // Mehr steht dem Gang nicht im Weg. Die Satteltasche allein reicht als
        // Grund loszulaufen — sie braucht weder Glocke noch Gehilfen, und ein
        // ausgegrauter Knopf hat sie einmal mitgenommen: Der Hinweis sagte
        // "dann eben nur die Satteltasche", der graue Knopf liess auch die
        // nicht zu.
        return null;
    }

    /// <summary>
    /// Was an diesem Gang nicht gehen wird — als Zusatz zum Hinweistext, nicht
    /// als Grund, den Knopf zu sperren.
    /// </summary>
    public string? Caveat() =>
        !RetainerList.IsOpen && SummoningBells.Nearest(config, vendors) == null
            ? "No summoning bell known yet, so only the saddlebag is sorted." +
              Environment.NewLine + "Walk past a bell once and it is remembered."
            : null;

    public void Start()
    {
        _rows.Clear();
        _rows.AddRange(OpenableRows());
        _at = 0;
        _visited = 0;
        _fetched = 0;
        _stowed = 0;
        _sawSaddlebag = false;
        stash.TakeTotals();
        _deadline = Environment.TickCount64 + TimeoutMs;
        _nextAt = 0;

        // Eine leere Liste ist hier kein Grund aufzuhoeren. Sie hiess einmal
        // "keine Gehilfen" und beendete den Gang, bevor er anfing — auch die
        // Satteltasche, fuer die gar kein Gehilfe noetig ist. Dabei sagt eine
        // leere Liste nur, dass das Spiel noch keine herausgegeben hat: Vor der
        // ersten Rufglocke einer Sitzung zaehlt <c>GetRetainerCount</c> null,
        // ob man nun acht Gehilfen hat oder keinen. Wer sie hat, erfaehrt es an
        // der Glocke; bis dahin wird nichts angenommen.
        if (_rows.Count == 0)
            Plugin.Log.Information(
                "[MasterBaiter] No retainers listed yet — the saddlebag first, " +
                "then a bell to find out whether there are any.");

        _step = Step.BagFetch;
        Status = "Sorting the saddlebag.";
    }

    /// <summary>Weiter zu den Gehilfen, oder Schluss.</summary>
    private void AfterSaddlebag(long now)
    {
        if (RetainerList.IsOpen)
        {
            _step = Step.Opening;
            _nextAt = now + StepDelayMs;
            return;
        }

        if (SummoningBells.Nearest(config, vendors) is not { } bell)
        {
            // Kein Weg zu den Gehilfen ist kein Fehlschlag, wenn die
            // Satteltasche schon erledigt ist.
            Plugin.Log.Information(
                "[MasterBaiter] No summoning bell known, so the retainers are skipped.");
            Finish();
            return;
        }

        travel.Start(bell);
        _step = Step.Travelling;
        Status = "Heading for a summoning bell.";
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
            Fail($"The retainer run ran out of time in step {_step}.");
            return;
        }

        if (now < _nextAt)
            return;

        switch (_step)
        {
            case Step.BagFetch:
                if (stash.Running)
                    return;

                stash.Start(restock, Stash.Saddlebag);
                _step = Step.BagStow;
                _nextAt = now + StepDelayMs;
                break;

            case Step.BagStow:
                if (stash.Running)
                    return;

                Collect();
                stash.StartStow(restock, Stash.Saddlebag);
                _step = Step.Travelling;
                _nextAt = now + StepDelayMs;

                // Die Reise erst starten, wenn das Wegraeumen durch ist —
                // sonst laeuft der Charakter los, waehrend noch Faecher
                // verschoben werden.
                _startedTravel = false;
                break;

            case Step.Travelling:
                if (stash.Running)
                    return;

                if (!_startedTravel)
                {
                    _startedTravel = true;
                    AfterSaddlebag(now);
                    return;
                }

                if (RetainerList.IsOpen)
                {
                    _step = Step.Opening;
                    _nextAt = now + StepDelayMs;
                    return;
                }

                // Die Reise meldet ihr Scheitern selbst; laeuft sie nicht mehr
                // und die Liste ist trotzdem zu, ist der Gang zu Ende.
                if (!travel.Running)
                    Fail("Never reached a summoning bell.");

                break;

            case Step.Opening:
                if (visit.Running)
                    return;

                if (Stash.Retainer.Ready)
                {
                    _step = Step.Fetching;
                    _nextAt = now + StepDelayMs;
                    return;
                }

                // Die Liste ist offen, also gibt das Spiel jetzt Auskunft. Erst
                // hier ist eine leere Antwort eine Antwort.
                if (_rows.Count == 0)
                {
                    _rows.AddRange(OpenableRows());
                    Plugin.Log.Information(
                        $"[MasterBaiter] The bell lists {_rows.Count} retainer(s) that can be opened.");
                }

                if (_at >= _rows.Count)
                {
                    Finish();
                    return;
                }

                Status = $"Retainer {_at + 1} of {_rows.Count}.";
                visit.Start(_rows[_at]);
                _nextAt = now + StepDelayMs;
                break;

            case Step.Fetching:
                if (stash.Running)
                    return;

                Collect();

                // Erst holen, dann wegraeumen: Was gerade geholt wurde, ist
                // gebraucht und faellt dem Wegraeumen nicht zum Opfer — das
                // nimmt ohnehin nur, was kein Fisch mehr braucht.
                stash.Start(restock, Stash.Retainer);
                _step = Step.Stowing;
                _nextAt = now + StepDelayMs;
                break;

            case Step.Stowing:
                if (stash.Running)
                    return;

                Collect();
                stash.StartStow(restock, Stash.Retainer);
                _step = Step.Leaving;
                _nextAt = now + StepDelayMs;
                break;

            case Step.Leaving:
                if (stash.Running || visit.Running)
                    return;

                Collect();

                if (RetainerList.IsOpen)
                {
                    _visited++;
                    _at++;
                    _step = Step.Opening;
                    _nextAt = now + BetweenRetainersMs;
                    return;
                }

                visit.Leave();
                _nextAt = now + StepDelayMs;
                break;
        }
    }

    /// <summary>Holt die Zahlen des letzten Lagers ab.</summary>
    private void Collect()
    {
        var (fetched, stowed) = stash.TakeTotals();
        _fetched += fetched;
        _stowed += stowed;

        if (_step is Step.BagStow or Step.Travelling)
            _sawSaddlebag = true;
    }

    private void Finish()
    {
        Collect();
        _step = Step.Done;

        // Wo gewesen, und was dabei herausgekommen ist. Eine Meldung, die nur
        // "fertig" sagt, laesst offen, ob etwas passiert ist.
        var places = new List<string>();
        if (_sawSaddlebag)
            places.Add("the saddlebag");
        if (_visited == 1)
            places.Add("1 retainer");
        else if (_visited > 1)
            places.Add($"{_visited} retainers");

        var where = places.Count == 0 ? "nowhere" : string.Join(" and ", places);

        Status = _fetched == 0 && _stowed == 0
            ? $"Sorted {where}: nothing needed moving."
            : $"Sorted {where}: {_fetched} fetched, {_stowed} put away.";

        Plugin.Log.Information($"[MasterBaiter] {Status}");
        ChatReport.Say(config, Status);
    }

    private void Fail(string reason)
    {
        _step = Step.Failed;
        Status = reason;
        Plugin.Log.Warning($"[MasterBaiter] Retainer run: {reason}");
        ChatReport.Warn(config, reason);
    }
}
