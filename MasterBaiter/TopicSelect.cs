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
    /// Waehlt die Zeile, die am ehesten in einen Warenladen fuehrt.
    /// Die Regel selbst steht in <see cref="MenuScoring"/>, damit sie ohne
    /// Spiel geprueft werden kann.
    /// </summary>
    public static int BestEntry(IReadOnlyList<string> entries)
        => MenuScoring.Best(entries, out _);

    /// <summary>Wie <see cref="BestEntry(IReadOnlyList{string})"/>, mit der Bewertung fuers Protokoll.</summary>
    public static int BestEntry(IReadOnlyList<string> entries, out string scores)
        => MenuScoring.Best(entries, out scores);
}
