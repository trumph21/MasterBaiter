using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Das Auswahlfenster, das viele Haendler vorschalten ("Purchase Items",
/// "Purchase Fieldcraft Tools", "Nothing" …). Ohne die richtige Zeile oeffnet
/// sich der Laden nie.
///
/// FFXIV benutzt dafuer zwei Addons: SelectString ohne Symbole, SelectIconString
/// mit. Haendlermenues sind meist die Symbolvariante. Beide tragen ihre Zeilen
/// im selben PopupMenu, deshalb genuegt eine Leseroutine fuer beide.
///
/// Aufbau aus ECommons (AddonMaster.SelectString).
/// </summary>
internal static unsafe class TopicSelect
{
    private static AtkUnitBase* Plain()
    {
        var ptr = Plugin.GameGui.GetAddonByName("SelectString");
        return ptr.IsNull || !ptr.IsVisible ? null : (AtkUnitBase*)ptr.Address;
    }

    private static AtkUnitBase* Icon()
    {
        var ptr = Plugin.GameGui.GetAddonByName("SelectIconString");
        return ptr.IsNull || !ptr.IsVisible ? null : (AtkUnitBase*)ptr.Address;
    }

    public static bool IsOpen => Plain() != null || Icon() != null;

    public static List<string> Entries()
    {
        var result = new List<string>();

        if (Plain() is var plain && plain != null)
        {
            var addon = (AddonSelectString*)plain;
            var count = addon->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
                result.Add(Read(addon->PopupMenu.PopupMenu.EntryNames[i].Value));
            return result;
        }

        if (Icon() is var icon && icon != null)
        {
            var addon = (AddonSelectIconString*)icon;
            var count = addon->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < count; i++)
                result.Add(Read(addon->PopupMenu.PopupMenu.EntryNames[i].Value));
        }

        return result;
    }

    private static string Read(byte* p)
        => p == null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint)p).TextValue;

    public static bool Select(int index)
    {
        if (index < 0)
            return false;

        var addon = Plain();
        if (addon == null)
            addon = Icon();
        if (addon == null)
            return false;

        AddonCallback.Fire(addon, true, index);
        return true;
    }

    /// <summary>
    /// Waehlt die Zeile, die am ehesten in einen Warenladen fuehrt. Zeilen mit
    /// Werkzeug oder Ausruestung werden abgewertet, denn Koeder liegen bei den
    /// allgemeinen Waren.
    /// </summary>
    public static int BestEntry(IReadOnlyList<string> entries)
        => BestEntry(entries, out _);

    /// <summary>Wie <see cref="BestEntry(IReadOnlyList{string})"/>, gibt zusaetzlich die Bewertung aus.</summary>
    public static int BestEntry(IReadOnlyList<string> entries, out string scores)
    {
        var best = -1;
        var bestScore = int.MinValue;
        var report = new List<string>();

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

            // Nicht nach "materia" oder "material" abwerten: Beim
            // Cosmocredit-Tausch liegen die Koeder ausgerechnet unter
            // "(Materials/Materia/Items)". Der Menuetext sagt eben nicht
            // zuverlaessig, was im Laden steht — deshalb steht die Bewertung im
            // Log, damit eine Fehlwahl nachvollziehbar bleibt.

            report.Add($"{entries[i]}={score}");

            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        scores = string.Join(", ", report);
        return best;
    }
}
