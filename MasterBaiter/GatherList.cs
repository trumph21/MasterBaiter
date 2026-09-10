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

    private const string FileName = "auto_gather_lists.json";

    /// <summary>
    /// Der Ordner, in dem Dalamud die Einstellungen der Plugins ablegt.
    /// </summary>
    private static string ConfigRoot
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher", "pluginConfigs");

    /// <summary>Ein Plugin, das Sammellisten fuehrt.</summary>
    internal readonly record struct Source(string InternalName, string Name, string Path, bool Loaded);

    /// <summary>
    /// Alle Plugins, die Sammellisten fuehren — geladene wie liegengebliebene.
    ///
    /// Nicht geladene bleiben in der Liste, aber als solche gekennzeichnet: Wer
    /// zwei Baue installiert hat, soll sehen, dass es zwei sind, statt sich zu
    /// wundern, warum seine Liste woanders auftaucht.
    /// </summary>
    public static List<Source> Candidates()
    {
        var root = ConfigRoot;
        var found = new List<Source>();

        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
            {
                var dir = Path.Combine(root, plugin.InternalName);
                var lists = Path.Combine(dir, FileName);
                if (File.Exists(lists) && File.Exists(Path.Combine(dir, "gather_groups.json")))
                    found.Add(new Source(plugin.InternalName, plugin.Name, lists, plugin.IsLoaded));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MasterBaiter] Could not look for gather list folders.");
        }

        // Geladene zuerst: Nur deren Datei sagt etwas ueber die Gegenwart.
        return found.OrderByDescending(s => s.Loaded).ThenBy(s => s.Name).ToList();
    }

    private static string? _found;

    /// <summary>
    /// Wo GatherBuddy seine Listen fuehrt — gesucht, nicht angenommen.
    ///
    /// Hier stand vier Tage lang fest "GatherBuddyReborn", und das war falsch:
    /// Ein eigener Bau heisst, wie sein Erbauer ihn nennt, und Dalamud legt
    /// dessen Einstellungen unter genau diesem Namen ab. Hier hiess er
    /// "NoriBuddy" — die Koederplanung las also einen vier Tage alten Stand,
    /// ohne dass irgendwo etwas fehlgeschlagen waere. Eine Datei, die es gibt
    /// und die sich lesen laesst, sieht nicht falsch aus.
    ///
    /// Erkannt wird am Fingerabdruck statt am Namen: GatherBuddy legt neben
    /// den Listen immer <c>gather_groups.json</c> ab. Und es muss geladen
    /// sein — der tote Ordner von gestern liegt ja noch da.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            if (_found != null)
                return _found;

            var root = ConfigRoot;
            var fallback = Path.Combine(root, "GatherBuddyReborn", FileName);

            if (Candidates().FirstOrDefault(c => c.Loaded) is { Path.Length: > 0 } live)
            {
                Plugin.Log.Information(
                    $"[MasterBaiter] Gather lists come from \"{live.Name}\" " +
                    $"({live.InternalName}): {live.Path}");

                return _found = live.Path;
            }

            Plugin.Log.Warning(
                "[MasterBaiter] No loaded plugin keeps gather lists. Falling back to " +
                $"{fallback}, which may be stale.");

            return fallback;
        }
    }

    /// <summary>Vergisst den gefundenen Ort, etwa wenn der Spieler ihn selbst setzt.</summary>
    public static void Rediscover() => _found = null;

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
