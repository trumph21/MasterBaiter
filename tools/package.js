// Baut das Auslieferungspaket in dist/ und die Repository-Datei repo.json.
//
// Der Grund fuer dieses Skript: README und Manifest lagen zweimal im Baum, in
// der Wurzel und noch einmal unter dist/. Zwei Kopien laufen auseinander — der
// Wurzel-README stand tagelang auf einem alten Stand, waehrend der
// ausgelieferte aktuell war. Jetzt gibt es je eine Quelle, und dist/ ist
// vollstaendig erzeugt.
//
//   README.md                      -> dist/MasterBaiter/README.md (ohne Bau-Abschnitt)
//   MasterBaiter/MasterBaiter.json -> dist/MasterBaiter/MasterBaiter.json
//   MasterBaiter/bin/*.dll         -> dist/MasterBaiter/MasterBaiter.dll
//   alles zusammen                 -> dist/MasterBaiter.zip
//   dasselbe Manifest              -> repo.json
//
// Zwei Dinge sind fuer Dalamud zwingend und leicht zu uebersehen:
//
//   1. Die Dateien muessen im WURZELVERZEICHNIS der ZIP liegen. Ein Unterordner
//      wird nicht ausgepackt, das Plugin gilt dann als fehlerhaft.
//   2. Der Downloadlink braucht einen festen Namen. Ein Link auf
//      MasterBaiter-0.3.2.zip zeigt nach der naechsten Version ins Leere, und
//      Dalamud meldet nur "Download fehlgeschlagen".
//
// Aufruf: node tools/package.js   (aus dem Projektstamm)

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const root = path.resolve(__dirname, "..");
const dist = path.join(root, "dist");
const stage = path.join(dist, "MasterBaiter");
const repoUrl = "https://github.com/trumph21/MasterBaiter";
const rawUrl = "https://raw.githubusercontent.com/trumph21/MasterBaiter/main";

function fail(message) {
  console.error(`package: ${message}`);
  process.exit(1);
}

// ---- Version aus der csproj, damit sie nur an einer Stelle steht ----
const csproj = fs.readFileSync(path.join(root, "MasterBaiter", "MasterBaiter.csproj"), "utf8");
const versionMatch = csproj.match(/<Version>(\d+)\.(\d+)\.(\d+)/);
if (!versionMatch) fail("no <Version> in MasterBaiter.csproj");
const version = versionMatch.slice(1, 4).join(".");

// ---- DLL ----
const dll = path.join(root, "MasterBaiter", "bin", "Release", "MasterBaiter.dll");
if (!fs.existsSync(dll)) fail("MasterBaiter/bin/Release/MasterBaiter.dll is missing — build first");

// ---- Manifest, Version gegenpruefen ----
const manifestPath = path.join(root, "MasterBaiter", "MasterBaiter.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
if (!manifest.AssemblyVersion.startsWith(version))
  fail(`version mismatch: csproj says ${version}, manifest says ${manifest.AssemblyVersion}`);

// ---- README ohne die Abschnitte, die nur das Repo angehen ----
const readme = fs.readFileSync(path.join(root, "README.md"), "utf8");
const shipped = readme
  .replace(/<!-- repo-only -->[\s\S]*?<!-- \/repo-only -->\n/g, "")
  .replace(/\n{3,}/g, "\n\n");

if (shipped.includes("Building from source")) fail("the build section was not stripped");

// ---- Schreiben ----
fs.rmSync(dist, { recursive: true, force: true });
fs.mkdirSync(stage, { recursive: true });
fs.copyFileSync(dll, path.join(stage, "MasterBaiter.dll"));
fs.copyFileSync(manifestPath, path.join(stage, "MasterBaiter.json"));
fs.writeFileSync(path.join(stage, "README.md"), shipped);

// Inhalt des Ordners, nicht der Ordner selbst — siehe Punkt 1 oben.
const zip = path.join(dist, "MasterBaiter.zip");
execFileSync("powershell.exe", [
  "-NoProfile", "-Command",
  `Compress-Archive -Path '${path.join(stage, "*")}' -DestinationPath '${zip}' -Force`,
], { stdio: "inherit" });

// ---- repo.json fuer die Plugin-Verwaltung in Dalamud ----
const download = `${repoUrl}/releases/latest/download/MasterBaiter.zip`;
const entry = {
  Author: manifest.Author,
  Name: manifest.Name,
  InternalName: manifest.InternalName,
  AssemblyVersion: manifest.AssemblyVersion,
  Description: manifest.Description,
  Punchline: manifest.Punchline,
  Changelog: manifest.Changelog,
  ApplicableVersion: manifest.ApplicableVersion,
  DalamudApiLevel: manifest.DalamudApiLevel,
  RepoUrl: repoUrl,
  Tags: manifest.Tags,
  // Dalamud zeigt das Bild im Installer. Fehlt die Datei, bleibt das Feld weg —
  // ein toter Link waere schlechter als das Fragezeichen.
  ...(fs.existsSync(path.join(root, "images", "icon.png"))
    ? { IconUrl: `${rawUrl}/images/icon.png` }
    : {}),
  AcceptsFeedback: manifest.AcceptsFeedback ?? false,
  IsHide: false,
  LastUpdate: Math.floor(Date.now() / 1000),
  DownloadCount: 0,
  DownloadLinkInstall: download,
  DownloadLinkUpdate: download,
  DownloadLinkTesting: download,
};

fs.writeFileSync(path.join(root, "repo.json"), JSON.stringify([entry], null, 2) + "\n");

const size = (fs.statSync(zip).size / 1024).toFixed(0);
console.log(`package: MasterBaiter ${version} -> dist/MasterBaiter.zip (${size} KB), repo.json updated`);
