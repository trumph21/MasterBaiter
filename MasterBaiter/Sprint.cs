using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MasterBaiter;

/// <summary>
/// Setzt Sprint ein, solange das Plugin den Charakter bewegt.
///
/// Sprint ist GeneralAction 4 (dahinter Action 3) — nachgeschlagen, nicht
/// geraten. Ob er gerade geht, beantwortet das Spiel selbst ueber
/// <c>GetActionStatus</c>; eine eigene Buchhaltung ueber Abklingzeiten waere
/// nur eine zweite, schlechtere Wahrheit.
///
/// Bewusst nur waehrend einer Reise: "immer wenn bereit" hiesse sonst auch
/// beim Angeln, im Kampf oder waehrend man selbst spielt. Wer laeuft, spart
/// Zeit; wer steht, verbrennt nur die Abklingzeit.
/// </summary>
internal static unsafe class Sprint
{
    private const uint SprintGeneralAction = 4;

    /// <summary>So oft wird geprueft. Haeufiger bringt nichts, die Abklingzeit ist lang.</summary>
    private const int CheckIntervalMs = 1000;

    private static long _nextCheck;

    public static void Tick(Configuration config, Travel travel, Route route, CosmicTravel cosmic)
    {
        if (!config.UseSprint)
            return;

        var now = Environment.TickCount64;
        if (now < _nextCheck)
            return;
        _nextCheck = now + CheckIntervalMs;

        // Nur wenn das Plugin tatsaechlich unterwegs ist.
        if (!travel.Running && !route.Running && !cosmic.Running)
            return;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return;

        // Im Kampf, beim Reiten, im Gespraech oder waehrend eines Ladevorgangs
        // hat Sprint nichts verloren — teils lehnt das Spiel ihn ohnehin ab.
        var conditions = Plugin.Condition;
        if (conditions[ConditionFlag.InCombat]
            || conditions[ConditionFlag.Mounted]
            || conditions[ConditionFlag.Casting]
            || conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.OccupiedInQuestEvent]
            || conditions[ConditionFlag.Occupied33])
            return;

        var manager = ActionManager.Instance();
        if (manager == null)
            return;

        // Status 0 heisst benutzbar. Alles andere — Abklingzeit, falscher
        // Zustand, nicht freigeschaltet — laesst uns in Ruhe warten.
        if (manager->GetActionStatus(ActionType.GeneralAction, SprintGeneralAction) != 0)
            return;

        if (manager->UseAction(ActionType.GeneralAction, SprintGeneralAction))
            Plugin.Log.Debug("[MasterBaiter] Sprint.");
    }
}
