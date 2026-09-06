using Newtonsoft.Json;

namespace MasterBaiter;

/// <summary>
/// Liest die Auto-Gather-Listen von GatherBuddy Reborn direkt aus deren
/// Konfigurationsdatei. GatherBuddys IPC gibt die Listen nicht heraus, die
/// Datei liegt aber offen im pluginConfigs-Verzeichnis.
/// </summary>
internal sealed class GatherList
{
    public sealed class Entry
    {
        [JsonProperty("Name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("Enabled")] public bool Enabled { get; set; }
        [JsonProperty("ItemIds")] public List<uint> ItemIds { get; set; } = [];
        [JsonProperty("EnabledItems")] public Dictionary<string, bool> EnabledItems { get; set; } = new();

        /// <summary>Items der Liste, die nicht einzeln abgewaehlt sind.</summary>
        public IEnumerable<uint> ActiveItemIds
            => ItemIds.Where(id => !EnabledItems.TryGetValue(id.ToString(), out var on) || on);
    }

    public static string DefaultPath
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs", "GatherBuddyReborn", "auto_gather_lists.json");

    public List<Entry> Lists { get; private set; } = [];
    public string? Error { get; private set; }
    public DateTime LoadedAt { get; private set; }

    public bool Load(string path)
    {
        Lists = [];
        Error = null;
        try
        {
            if (!File.Exists(path))
            {
                Error = $"File not found: {path}";
                return false;
            }

            // GatherBuddy schreibt teils mit BOM
            var text = File.ReadAllText(path).TrimStart('﻿');
            Lists = JsonConvert.DeserializeObject<List<Entry>>(text) ?? [];
            LoadedAt = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Plugin.Log.Error(ex, "Could not read the auto-gather list.");
            return false;
        }
    }

    /// <summary>Items aller aktivierten Listen, oder aller Listen wenn keine aktiviert ist.</summary>
    public IEnumerable<uint> RelevantItemIds(bool onlyEnabledLists)
    {
        var lists = onlyEnabledLists ? Lists.Where(l => l.Enabled).ToList() : Lists;
        return lists.SelectMany(l => l.ActiveItemIds).Distinct();
    }
}
