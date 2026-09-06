namespace MasterBaiter;

/// <summary>
/// Kurze Rueckmeldung im Spielchat.
///
/// Bisher landete alles im Log, und wer nicht daran denkt, /xllog zu oeffnen,
/// erfaehrt nie, ob ein Durchlauf etwas gekauft hat oder woran er scheiterte.
/// Ein Satz je abgeschlossenem Vorgang genuegt dafuer — je Kauf waere es
/// Gespamme, und der Chat gehoert dem Spieler, nicht dem Plugin.
///
/// Fehler werden getrennt ausgegeben, damit sie sich im Chat abheben.
/// </summary>
internal static class ChatReport
{
    private const string Tag = "MasterBaiter";

    public static void Say(Configuration config, string message)
    {
        if (!config.ChatFeedback || message.Length == 0)
            return;

        Plugin.ChatGui.Print(message, Tag, 45);
    }

    public static void Warn(Configuration config, string message)
    {
        if (!config.ChatFeedback || message.Length == 0)
            return;

        Plugin.ChatGui.PrintError(message, Tag, 45);
    }
}
