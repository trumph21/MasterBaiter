using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MasterBaiter;

/// <summary>
/// Ruft ein Reittier, solange das Plugin den Charakter ueber laengere Strecken
/// bewegt.
///
/// Mount Roulette ist GeneralAction 9, Flying Mount Roulette 24 —
/// nachgeschlagen im GeneralAction-Blatt, nicht geraten. Ob das Rufen hier
/// erlaubt ist, beantwortet das Spiel selbst ueber <c>GetActionStatus</c>: In
/// Staedten, in Instanzen und waehrend eines Gespraechs ist es das nicht, und
/// eine eigene Liste solcher Orte waere eine zweite, schlechtere Wahrheit.
///
/// Nur ab einer Mindeststrecke: Aufsitzen kostet gut zwei Sekunden. Fuer
/// zwanzig Meter ist das ein Verlust, fuer zweihundert ein Gewinn.
/// </summary>
internal static unsafe class Mount
{
    private const uint MountRoulette = 9;
    private const uint FlyingMountRoulette = 24;

    /// <summary>Ab dieser Entfernung lohnt das Aufsitzen.</summary>
    private const float WorthMountingDistance = 60f;

    private const int CheckIntervalMs = 2000;

    private static long _nextCheck;

    public static void Tick(Configuration config, Travel travel, Route route, CosmicTravel cosmic)
    {
        if (!config.UseMount)
            return;

        var now = Environment.TickCount64;
        if (now < _nextCheck)
            return;
        _nextCheck = now + CheckIntervalMs;

        if (!travel.Running && !route.Running && !cosmic.Running)
            return;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return;

        // Schon oben, oder gerade nicht in der Lage dazu.
        var conditions = Plugin.Condition;
        if (conditions[ConditionFlag.Mounted]
            || conditions[ConditionFlag.InCombat]
            || conditions[ConditionFlag.Casting]
            || conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.OccupiedInQuestEvent]
            || conditions[ConditionFlag.Occupied33]
            || conditions[ConditionFlag.Jumping])
            return;

        // Fuer den letzten Schritt zum Haendler lohnt es nicht.
        if (travel.DistanceToTarget is { } left && left < WorthMountingDistance)
            return;

        var manager = ActionManager.Instance();
        if (manager == null)
            return;

        // Fliegen, wo es freigeschaltet ist — sonst zu Fuss aufsitzen.
        var action = config.UseFlight && Flight.AllowedHere ? FlyingMountRoulette : MountRoulette;

        if (manager->GetActionStatus(ActionType.GeneralAction, action) != 0)
        {
            // Das fliegende Roulette kann abgelehnt werden, wo das normale geht.
            if (action == MountRoulette
                || manager->GetActionStatus(ActionType.GeneralAction, MountRoulette) != 0)
                return;

            action = MountRoulette;
        }

        if (manager->UseAction(ActionType.GeneralAction, action))
            Plugin.Log.Information(
                $"[MasterBaiter] Mounting up ({(action == FlyingMountRoulette ? "flying" : "ground")}).");
    }
}
