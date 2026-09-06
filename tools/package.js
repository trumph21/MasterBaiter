// Baut das Auslieferungspaket in dist/.
//
// Der Grund fuer dieses Skript: README und Manifest lagen zweimal im Baum, in
// der Wurzel und noch einmal unter dist/. Zwei Kopien laufen auseinander — der
// Wurzel-README stand tagelang auf einem alten Stand, waehrend der
// ausgelieferte aktuell war. Jetzt gibt es je eine Quelle, und dist/ ist
// vollstaendig erzeugt.
//
//   README.md                     -> dist/MasterBaiter/README.md (ohne Bau-Abschnitt)
//   MasterBaiter/MasterBaiter.json -> dist/MasterBaiter/MasterBaiter.json
//   MasterBaiter/bin/*.dll         -> dist/MasterBaiter/MasterBaiter.dll
//   alles zusammen                 -> dist/MasterBaiter-<version>.zip
//
// Aufruf: node tools/package.js   (aus dem Projektstamm)

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const root = path.resolve(__dirname, "..");
const dist = path.join(root, "dist");
const stage = path.join(dist, "MasterBaiter");

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
const dll = path.join(root, "MasterBaiter", "bin", "MasterBaiter.dll");
if (!fs.existsSync(dll)) fail("MasterBaiter/bin/MasterBaiter.dll is missing — build first");

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

const zip = path.join(dist, `MasterBaiter-${version}.zip`);
execFileSync("powershell.exe", [
  "-NoProfile", "-Command",
  `Compress-Archive -Path '${stage}' -DestinationPath '${zip}' -Force`,
], { stdio: "inherit" });

const size = (fs.statSync(zip).size / 1024).toFixed(0);
console.log(`package: MasterBaiter ${version} -> dist/MasterBaiter-${version}.zip (${size} KB)`);
