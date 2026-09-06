# MasterBaiter

A Dalamud plugin for Final Fantasy XIV.

It reads the auto-gather list from **GatherBuddy Reborn**, works out which fishing
bait those fish need, compares that against your inventory, and shows what is
missing — including where to buy it and what it costs. It travels to the vendors
on its own, restocks at gil shops and scrip exchanges, and can fill the rest at
the market board.

---

## What is new in 0.3.4

**All 188 baits and lures, if you want them.** `Show all fishing tackle` under
Options lists everything the game has — 159 baits and 29 lures — instead of only
the ones your current fish need.

The added entries start at **target 0** and are never bought until you set a
number. With the normal defaults the plugin would otherwise have tried to stock
128 more baits at once, three of which cost 99,999 gil each.

---

## What is new in 0.3.3

**A line in chat when something finishes.** Every result used to live in
`/xllog`, where nobody looks unless something is already wrong. A run, a scrip
sweep, a market board visit and a route each report one line now; failures stand
out as errors. Toggleable under Options, and only you see it.

**Tests for the two rules that cost something when they are wrong** — which
market board listing to buy, and which menu entry leads to the bait. Both had
misfired during development: a stack larger than the shortfall must never win,
and the Cosmocredit bait sits under "(Materials/Materia/Items)", so scoring
"Materia" down would be wrong however sensible it sounds. Those cases are now
written down rather than rediscovered.

---

## What is new in 0.3.2

**Routes reach the Cosmic Exploration planets.** `Go` already got there, but the
route planner asked a different question about reachability and quietly left
those vendors out. All three places — the button, the planner and the self-check
— now ask the same one.

**Several stalls are gone.** A scrip exchange that opened on an empty tab was
skipped; an open shop window made Lifestream refuse the next teleport; an
aethernet hop fired before the teleport had landed; and a market board search
that went unanswered left the route waiting forever. Each of those now retries
or moves on, and says so.

**207 bogus teleport points removed.** The game data names a nearby city
aetheryte for many zones that have none of their own. That is not the same as
being able to get there: teleporting to Foundation leaves you a zone away from
The Firmament, which is now reported as unreachable instead of failing after the
teleport.

**Idyllshire first among scrip exchanges.** It carries the bait of every
expansion, so one stop replaces several.

**New settings.** `Speed %` sets how briskly the plugin acts — 100 is the
default, 50 twice as fast. `Use Sprint` uses Sprint while travelling.

---

## What is new in 0.3.1

**`Export log to desktop`** under Options → Diagnostics, so a problem can be
reported without digging through `dalamud.log` — and without passing on what
other plugins logged.

---

## What is new in 0.3.0

**Cosmic Exploration works.** Baits sold on the planets used to be listed as
unreachable, because no aetheryte goes there. The plugin now teleports to
Bestways Burrow, walks to Drivingway the Moon Rover in Mare Lamentorum, picks
the planet, blasts off and buys at the vendor — in one go.

**Greeting boxes no longer block travel.** An NPC that says something before
opening its shop was previously talked over again and again, which reset the
dialogue every time. Any NPC with a greeting was effectively unreachable.

**Purchases no longer fail silently.** Buying started in the same instant the
shop window opened, when it was still empty, so nothing was found and nothing
was said. It now waits for the shop to list its items, and if it still buys
nothing it writes down why.

**The dialog choice is explained.** When an NPC offers several shops, the log
shows how each option scored, so a wrong pick can be understood instead of
guessed at.

**Delays vary instead of being fixed.** Fixed millisecond values were a bet on
the game always answering at the same speed, and it does not. Note that this
makes the plugin no harder to detect — it only makes it less brittle.

---

## What is new in 0.2.0

**Lures are counted separately from bait.** They cost ten to a thousand times as
much, so 300 of them is never what you meant. Bait keeps its own target, lures
get theirs, 10 by default.

**Scrip exchanges work.** The exchange window only ever shows one subcategory,
and bait is spread over several, so a single run found almost nothing before.
`Buy missing` now walks every category and subcategory that carries bait and
buys on each.

**Scrip and menu vendors are reachable.** Many NPCs list a dialog menu rather
than a shop, and their bait looked like it had no vendor at all. Following that
menu found 28 more shops; every bait now has either a vendor or a recipe.

**Vendors are preferred by currency:** gil shop, then Cosmic Exploration, then
scrip exchange. Gil can be earned back, scrips cannot.

**The market board is supported.** Off by default. It travels there, searches,
and buys within a price per item and a total per run — and never a stack larger
than what you are missing.

**Two tabs.** The buttons you press while playing stay with the table;
everything you set once moved to Options.

**A crash is fixed.** The teleport list was rebuilt hundreds of times per frame
from the window drawing code, which could take the game down during a zone
change. It is now read on a timer and only while logged in.

---

## Dependencies

All three are required. Install them before this plugin.

| Plugin | Needed for |
|---|---|
| **GatherBuddy Reborn** | The bait list. It is read from GatherBuddy's auto-gather list, so without it the table stays empty. |
| **vnavmesh** | Walking to a vendor (`Go`, `Run route`). |
| **Lifestream** | Teleporting between zones (`Go`, `Run route`). |

If vnavmesh or Lifestream is missing, the plugin says so in its window and in
`/xllog`, and the travel buttons stay disabled. The rest of the window keeps
working, but you would be buying at vendors you walked to yourself.

Requires Dalamud API level 15 or newer.

---

<!-- repo-only -->
## Building from source

```bash
cd MasterBaiter && dotnet build -c Release
```

Needs the **.NET 10 SDK** — Dalamud API 15 runs on .NET 10, not 9 — and an
XIVLauncher installation, because the build references the Dalamud libraries in
`%APPDATA%XIVLauncheraddonHooksdev`. The result lands in
`MasterBaiter/bin/`.

Tests cover the rules where a mistake is expensive or silent — which listing to
buy, which menu entry leads to the bait, how the delays scale:

```bash
cd tests/MasterBaiter.Tests && dotnet test -c Release
```

Use `-c Release`. A debug run writes to `bin/Debug/` and leaves the
release build alone, which is what a running game has loaded.

Packaging is `node tools/package.js`: it copies the DLL and the manifest,
generates the shipped README from this one, and writes the ZIP to `dist/`.
Nothing under `dist/` is kept in the repository — the ZIP is published as a
release asset.

Two data files under `data/` are embedded into the DLL and regenerated by the
scripts in `tools/`: the fish-to-bait table from GatherBuddy Reborn, and vendor
coordinates for the NPCs the game’s own location sheet does not place.

---

<!-- /repo-only -->
## Installation

### The easy way: add the repository

In game, open Dalamud settings:

```
/xlsettings  ->  tab "Experimental"  ->  "Custom Plugin Repositories"
```

Paste this URL, press **+**, then **Save**:

```
https://raw.githubusercontent.com/trumph21/MasterBaiter/main/repo.json
```

**MasterBaiter** then appears in the plugin installer under *All Plugins* and
updates itself like any other plugin.

### The manual way

Use this if you would rather not add a repository, or you are working on the
plugin yourself.

1. **Unpack** `MasterBaiter.zip` from the
   [releases page](https://github.com/trumph21/MasterBaiter/releases) into a
   folder somewhere permanent. The folder must be **named exactly
   MasterBaiter** — Dalamud rejects the path otherwise, and moving it later
   breaks the link:

   ```
   C:\Plugins\MasterBaiter\
   ```

2. **Register the path.** In game, open Dalamud settings:

   ```
   /xlsettings  →  tab "Experimental"  →  "Dev Plugin Locations"
   ```

   Enter the full path to the DLL and press the **+** button:

   ```
   C:\Plugins\MasterBaiter\MasterBaiter.dll
   ```

3. **Enable it.** Open the plugin installer, find **MasterBaiter** among the dev
   plugins, and switch it on.

Installed this way, the plugin does not update itself.

If nothing appears, check `/xllog` — a failed load always leaves a message there.

**Commands:** `/masterbaiter` or `/mbait`

---

## Using it

The window has two tabs. **Baits** is the working view: the buttons you press
while playing, and the table. **Options** holds everything you set once —
stock levels, the bait list, travel, the market board, and the diagnostics.

Under Options, set a **Bait target**, 300 by default, and a **Lure target**, 10
by default. Lures cost ten to a thousand times what bait does, so they get their
own number. The split follows the game data, not the name — the item description
and an internal marker have to agree, and the plugin logs both counts so a
mismatch after a patch is visible.

Where a bait is sold by more than one vendor, the plugin prefers them in this
order: **gil shop, then Cosmic Exploration, then scrip exchange**. Gil can be
earned back; scrips cannot, so anything available for gil is bought for gil.
This decides both the `Go` button and the order of stops in a route.

The table lists every bait your fish need:

| Column | Meaning |
|---|---|
| Bait | Tick the box to skip this bait when buying |
| Fish | How many fish on your list use it — hover for the names. A dash means no fish on your list needs it |
| Inventory | How many you carry |
| Target | Per-bait override of the global target or lure target |
| Missing | What would be bought |
| Price | Gil or currency, live from the shop when one is open. A `~` marks a market board price seen during a run — hover for how many listings and how long ago |
| Vendor | Where to get it — hover for names, zones and coordinates |

**At a vendor** with the shop window open, `Buy missing` tops everything up. The
game caps purchases at 99 per transaction, so larger amounts are bought in
batches automatically.

**At a scrip exchange** the same button does more work. That window only ever
shows one subcategory at a time, and bait is spread over several of them
("Lv. 50 Materials/Bait", "Lv. 90 Bait/Tokens" and so on). `Buy missing` walks
through every category and every subcategory in turn and buys on each, so one
click covers the whole shop instead of the tab that happened to be open.

**Market board** is off by default. Switched on, two limits appear next to the
checkbox — **gil per item** (1000 by default) and **gil per run** (100000) — and
baits that no vendor sells show **market** in the Vendor column with their own
`Go` button. `Run route` adds a market board as its last stop, last because it
is the only stop that spends gil at prices other players set.

At the board, `Buy on market board` searches for each bait in turn, waits for
the listings, buys, and moves on. Nothing about it is manual. If the game does
not answer within a few seconds it says so and waits for you to search that bait
yourself; the purchase then continues as normal.

Three rules decide what it buys, and all three are hard:

- **Never above your price per item.**
- **Never a stack larger than what you are still missing.** Listings are
  indivisible, so a stack of 99 when you need 10 is not a bargain — it is ten
  times the bill for goods you did not want. Such a listing is skipped even if
  it is the only one on offer.
- **Never past your total for the run**, counting the whole stack and your
  actual gil.

Among the listings that pass, it takes the cheapest per item; at equal prices
the larger stack, which gets you there in fewer purchases. Every listing is
examined, not just the cheapest — the cheapest is often a big stack that the
size rule rejects.

When nothing is bought, the log says which of the three rules stopped it, since
each one points at a different setting:

```
Spinner: skipped. Cheapest listing is 4500 gil each, above the 1000 gil limit.
Yumizuno: skipped. Cheapest listing is 3899 gil each, but the smallest stack
          on offer is 10 and only 4 are missing.
Topwater Frog: skipped. Cheapest listing is 3999 gil each, but no stack of 4
          or fewer fits the remaining 13018 gil.
```

**`Go`** next to a vendor teleports there and walks to the NPC. With
`buy on arrival` ticked it also opens the shop and buys.

Where a bait has several vendors, `Go` follows the currency order — gil, then
Cosmic Exploration, then scrips — while `Run route` picks whoever covers the
most baits in one stop. The two lists are therefore sorted differently on
purpose; the route tooltip names each stop's shop type so the difference is
visible.

**Cosmic Exploration** has no aetheryte, so `Go` on a bait sold there rides
instead of teleporting: to Bestways Burrow, over to Drivingway the Moon Rover,
onto the planet you picked under Options, and on to the vendor. `Pick the planet
automatically` is off by default — switched on, the plugin clicks the planet and
Blast Off for you; switched off, you do those two clicks and it handles the rest.

**`Run route`** picks the smallest set of vendors that covers everything missing
and visits them in turn. The button shows how many stops that will be; hover to
see them. `Stop route` aborts at any time.

---

## Troubleshooting

The three buttons below live under **Options → Diagnostics**.

**`Self-check`** goes through every bait and writes a report to `/xllog`: what is
reachable, what is only craftable, what has no source at all, and which zones it
cannot reach. Start here when something looks wrong.

**`Dump shop`** writes out the open shop window: every entry with its
purchase index, price and currency. At a scrip exchange it also names the
category and tab you are on.

**`Export log to desktop`** writes a text file to your desktop with this
plugin's log lines and your settings. Send that when reporting a problem —
it contains only MasterBaiter's own lines. The full `dalamud.log` also holds
everything other plugins wrote, character names and party data included, so it
is not what you want to hand around.

**`Dump windows`** writes every visible game window with its raw values to the
log. Useful if a dialog is not being recognised.

**The load message is worth a glance.** It reports how many lures were found and
by which of the two signals, how many vendors were resolved, and how many market
boards are known. A zero where you expect a number is the fastest sign that a
game patch has moved something.

---

## Known limits

**Market board locations come from two sources.** Market boards are not in the
game's location sheet at all, so the plugin ships coordinates read out of the
map files for the seven cities you can teleport to. Anywhere else — Tuliyollal
and Solution Nine place theirs differently — the plugin remembers the board once
you have stood near it, and can travel there from then on.

**The vendor location table is a snapshot.** Some NPCs are not in the game's own
location data, so their coordinates ship with the plugin. After a patch that adds
baits or moves vendors, that table can go stale until it is regenerated.

**Cosmic Exploration areas cannot be reached by teleport.** Vendors there are
reported as unreachable rather than travelled to.

**Some coordinates are estimated.** Where the game data has no height, the
navmesh is asked for the floor. In multi-level areas the path can end up on the
wrong one.

**Only the first 100 market board listings are seen**, and the plugin waits one
and a half seconds for them to arrive before deciding. On a slow connection it
may settle for a listing that was the cheapest so far rather than the cheapest
there is.

**Market board prices are remembered for the session only.** A price from
yesterday is not information, it is a trap — after a restart the column shows
`—` again until the next run.

**The shop behind an NPC's menu is picked by its wording.** Where an NPC offers
several shops, the plugin scores the menu entries for words like "items" or
"bait". The wording does not reliably say what is inside — at the Cosmocredit
Exchange the bait sits under "(Materials/Materia/Items)" — so a wrong pick is
possible. The log shows the scores when it happens.

**Clicking the planet is a simulated event.** The planet window does not accept
the usual callbacks, so the click is assembled and handed to the game. It works,
but it is the one place where a game update could turn a click into a crash.
That is why the option exists and why it is off by default.

**Travel is unattended automation.** It moves your character between zones and
spends gil. Watch it the first few times. Varying the delays does not hide any
of this: using a plugin at all is what carries the risk, not how evenly it
clicks.

---

## License

MIT — see [LICENSE](LICENSE).
