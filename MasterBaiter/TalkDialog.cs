using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Der Sprechkasten, den viele NPCs vorschalten ("Hey, you. Need a ride?").
///
/// Er ist keine Auswahl und kein Laden, sondern will nur weitergeklickt werden.
/// Wer ihn nicht kennt, haelt den Besuch fuer gescheitert und spricht erneut an
/// — was den Kasten von vorn beginnen laesst. Ein NPC mit Begruessung ist damit
/// unerreichbar, obwohl alles andere stimmt.
/// </summary>
internal static unsafe class TalkDialog
{
    private const int TextIndex = 0;
    private const int SpeakerIndex = 1;

    public static AtkUnitBase* Get()
    {
        var ptr = Plugin.GameGui.GetAddonByName("Talk");
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsReady ? addon : null;
    }

    public static bool IsOpen => Get() != null;

    /// <summary>Wer gerade spricht und was, fuer das Protokoll.</summary>
    public static string Describe()
    {
        var addon = Get();
        if (addon == null)
            return string.Empty;

        var values = addon->AtkValuesSpan;
        return $"{Read(values, SpeakerIndex)}: {Read(values, TextIndex)}";
    }

    private static string Read(Span<AtkValue> values, int index)
    {
        if (index >= values.Length)
            return string.Empty;
        var value = values[index];
        return value.String.Value == null
            ? string.Empty
            : MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
    }

    /// <summary>Klickt den Kasten weiter.</summary>
    public static bool Advance()
    {
        var addon = Get();
        if (addon == null)
            return false;

        AddonCallback.Fire(addon, true, 0);
        return true;
    }
}
