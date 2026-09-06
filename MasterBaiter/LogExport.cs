using System.Text;

namespace MasterBaiter;

/// <summary>
/// Schreibt die eigenen Logzeilen als Datei auf den Schreibtisch, damit jemand
/// sie weitergeben kann.
///
/// Bewusst nur die eigenen: In dalamud.log stehen auch die Zeilen aller anderen
/// Plugins, und darin finden sich Charakternamen, Gruppendaten und was
/// Synchronisationswerkzeuge sonst noch protokollieren. Wer seine ganze Datei
/// verschickt, gibt mehr preis, als er meint. Gefiltert bleibt genau das
/// uebrig, was zur Fehlersuche taugt.
///
/// Der Windows-Benutzername wird zusaetzlich aus Pfaden entfernt — er steht
/// sonst in jeder Zeile, die einen Dateipfad nennt.
/// </summary>
internal static class LogExport
{
    private const string Marker = "[MasterBaiter]";

    /// <summary>Erzeugt die Datei und gibt ihren Pfad zurueck, oder null bei Fehlschlag.</summary>
    public static string? Run(Configuration config, VendorIndex vendors, out string message)
    {
        var source = FindLog();
        if (source == null)
        {
            message = "Could not find dalamud.log.";
            return null;
        }

        try
        {
            var lines = ReadOwnLines(source);
            var target = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"MasterBaiter-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var text = new StringBuilder();
            text.AppendLine(Header(config, vendors, lines.Count));
            foreach (var line in lines)
                text.AppendLine(Redact(line));

            File.WriteAllText(target, text.ToString());
            message = $"{lines.Count} lines written to the desktop.";
            Plugin.Log.Information($"[MasterBaiter] Log exported to {Redact(target)}");
            return target;
        }
        catch (Exception ex)
        {
            message = $"Could not write the file: {ex.Message}";
            Plugin.Log.Warning($"[MasterBaiter] Log export failed: {ex}");
            return null;
        }
    }

    private static string? FindLog()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidate = Path.Combine(appData, "XIVLauncher", "dalamud.log");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Liest die Datei mit, waehrend Dalamud weiterschreibt. Ohne das geteilte
    /// Oeffnen scheitert der Zugriff, solange das Spiel laeuft — also immer.
    /// </summary>
    private static List<string> ReadOwnLines(string path)
    {
        var result = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
            if (line.Contains(Marker, StringComparison.Ordinal))
                result.Add(line);

        return result;
    }

    private static string Header(Configuration config, VendorIndex vendors, int lineCount)
    {
        var version = typeof(LogExport).Assembly.GetName().Version?.ToString() ?? "?";
        return $"""
                MasterBaiter {version} — log export, {DateTime.Now:yyyy-MM-dd HH:mm}
                {lineCount} lines, filtered to this plugin only.

                Bait target {config.DefaultTarget}, lure target {config.DefaultLureTarget}
                Enabled lists only: {config.OnlyEnabledLists}
                Buy on arrival: {config.BuyOnArrival}
                Market board: {config.UseMarketBoard} (max {config.MarketMaxUnitPrice} gil per item, {config.MarketMaxGilPerRun} per run)
                Cosmic planet: {config.CosmicPlanet}, auto pick: {config.AutoSelectPlanet}
                Ignored baits: {config.Ignored.Count}, per-bait targets: {config.Targets.Count}
                Lures known: {Tackle.LureCount}, vendor entries: {vendors.VendorCount}, teleport destinations: {Teleportable.Count}

                ----------------------------------------------------------------

                """;
    }

    /// <summary>Nimmt den Windows-Benutzernamen aus Pfaden heraus.</summary>
    private static string Redact(string line)
    {
        var user = Environment.UserName;
        return string.IsNullOrWhiteSpace(user)
            ? line
            : line.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
    }
}
