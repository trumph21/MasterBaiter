using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Listet alle gerade sichtbaren Fenster mit Namen und Rohwerten ins Log.
///
/// Der Grund fuer dieses Werkzeug: An mehreren Stellen wurde ein Fenster nicht
/// erkannt, weil ich den Addon-Namen geraten habe — SelectString statt
/// SelectIconString etwa. Mit dieser Ausgabe muss nichts mehr geraten werden.
/// </summary>
internal static unsafe class AddonDump
{
    private const int MaxValues = 20;

    public static void Run()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            Plugin.Log.Warning("[MasterBaiter] Addon dump: no AtkStage.");
            return;
        }

        var manager = stage->RaptureAtkUnitManager;
        if (manager == null)
        {
            Plugin.Log.Warning("[MasterBaiter] Addon dump: no unit manager.");
            return;
        }

        var units = manager->AllLoadedUnitsList;
        var report = new StringBuilder();
        report.AppendLine("[MasterBaiter] Visible windows:");

        var shown = 0;
        for (var i = 0; i < units.Count; i++)
        {
            var addon = units.Entries[i].Value;
            if (addon == null || !addon->IsVisible)
                continue;

            var name = addon->NameString;
            if (string.IsNullOrEmpty(name))
                continue;

            shown++;
            report.Append($"  {name}");

            var values = addon->AtkValuesSpan;
            if (values.Length > 0)
            {
                var count = Math.Min(values.Length, MaxValues);
                report.Append($"  ({values.Length} values)");
                for (var v = 0; v < count; v++)
                {
                    var value = values[v];
                    var text = value.Type switch
                    {
                        AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
                            => "\"" + Truncate(value.String.ToString()) + "\"",
                        AtkValueType.Int => value.Int.ToString(),
                        AtkValueType.UInt => value.UInt.ToString(),
                        AtkValueType.Bool => value.Byte != 0 ? "true" : "false",
                        _ => string.Empty,
                    };
                    if (text.Length > 0)
                        report.Append($" [{v}]={text}");
                }
            }

            report.AppendLine();
        }

        if (shown == 0)
            report.AppendLine("  (none)");

        Plugin.Log.Information(report.ToString().TrimEnd());
    }

    private static string Truncate(string s)
        => s.Length <= 40 ? s : s[..40] + "…";
}
