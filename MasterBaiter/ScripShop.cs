using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace MasterBaiter;

/// <summary>
/// Zugriff auf die beiden Auswahlfelder des Scrip-Tauschs ("Item Exchange").
///
/// Das Fenster zeigt immer nur eine Unterkategorie auf einmal. Ein Koeder, der
/// unter "Purple Scrip Exchange (Lv. 90 Bait/Tokens)" liegt, steht schlicht
/// nicht in den AtkValues, solange ein anderer Reiter offen ist. Wer nur das
/// sichtbare Blatt liest, haelt ihn fuer nicht vorhanden.
///
/// Beides laesst sich am Agenten umschalten:
///   AgentInclusionShop.SelectCategory(index)     die obere Auswahl
///   AgentInclusionShop.Data.SelectSubCategory(t) der Reiter darunter
///
/// Aufbau aus FFXIVClientStructs (AgentInclusionShop, AgentData).
/// </summary>
internal static unsafe class ScripShop
{
    private static AgentInclusionShop* Agent()
    {
        var agent = AgentInclusionShop.Instance();
        return agent == null || agent->Data == null ? null : agent;
    }

    public static bool Available => Agent() != null;

    /// <summary>Anzahl der Eintraege in der oberen Auswahl.</summary>
    public static int CategoryCount
    {
        get
        {
            var agent = Agent();
            return agent == null ? 0 : agent->Data->CategoryCount;
        }
    }

    /// <summary>Anzahl der Reiter, die zur gewaehlten Kategorie sichtbar sind.</summary>
    public static int SubCategoryCount
    {
        get
        {
            var agent = Agent();
            return agent == null ? 0 : agent->Data->VisibleSubCategoryCount;
        }
    }

    public static int SelectedCategory
    {
        get
        {
            var agent = Agent();
            return agent == null ? -1 : agent->Data->SelectedCategoryIndex;
        }
    }

    public static int SelectedSubCategory
    {
        get
        {
            var agent = Agent();
            return agent == null ? -1 : agent->Data->SelectedSubCategoryTab;
        }
    }

    public static string CategoryName(int index)
    {
        var agent = Agent();
        if (agent == null)
            return $"category {index}";

        var categories = agent->Data->Categories;
        if (index < 0 || index >= categories.Length || index >= agent->Data->CategoryCount)
            return $"category {index}";

        return categories[index].Name.ToString();
    }

    public static string SubCategoryName(int category, int tab)
    {
        var agent = Agent();
        if (agent == null)
            return $"tab {tab}";

        var categories = agent->Data->Categories;
        if (category < 0 || category >= categories.Length)
            return $"tab {tab}";

        ref var cat = ref categories[category];
        if (cat.SubCategories == null || tab < 0 || tab >= cat.SubCategoryCount)
            return $"tab {tab}";

        return cat.SubCategories[tab].Name.ToString();
    }

    public static bool SelectCategory(int index)
    {
        var agent = Agent();
        if (agent == null || index < 0 || index >= agent->Data->CategoryCount)
            return false;

        agent->SelectCategory((byte)index);
        return true;
    }

    public static bool SelectSubCategory(int tab)
    {
        var agent = Agent();
        if (agent == null || tab < 0 || tab >= agent->Data->VisibleSubCategoryCount)
            return false;

        return agent->Data->SelectSubCategory((byte)tab);
    }
}
