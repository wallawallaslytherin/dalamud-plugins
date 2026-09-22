# Gearset Organizer

A standalone Dalamud plugin for previewing and organizing saved gearsets by role, with guarded undo. Previewing, sorting, reference preservation, and undo run inside Gearset Organizer. No companion plugins are required.

## Installation

Follow the [repository installation instructions](../../README.md#install), then find **Gearset Organizer** in `/xlplugins`. Requires Dalamud API 15; no other plugin is required.

## Use

1. Enter `/gearsets` to open the **Gearset Organizer** window. The first explicit opening reads a preview; it does not sort anything.
2. Review the current and proposed gearset numbers. **Refresh preview** reads a new plan after any gearset, equipment, hotbar, or macro changes.
3. Select **Sort gearsets** to apply the displayed preview. It is unavailable when the list is already sorted or a blocker is present.
4. Select **Undo last sort** to restore the ordering from the latest retained successful sort, when the current state still matches its completed result.

`/gearsets` also closes the window. `/gearsets status` and `/gearsets test` provide read-only diagnostics. Sorting and undo are explicit window actions. The window starts closed when the plugin loads; there is no automatic sorting or background polling.

## Order

Only existing gearsets are included, with consecutive numbers:

| Group | Job order |
| --- | --- |
| Tanks | Paladin, Warrior, Dark Knight, Gunbreaker |
| Healers | White Mage, Scholar, Astrologian, Sage |
| Melee DPS | Monk, Dragoon, Ninja, Samurai, Reaper, Viper |
| Physical ranged DPS | Bard, Machinist, Dancer |
| Magical ranged DPS | Black Mage, Summoner, Red Mage, Pictomancer |
| Limited jobs | Blue Mage, Beastmaster |
| Crafters | Carpenter, Blacksmith, Armorer, Goldsmith, Leatherworker, Weaver, Alchemist, Culinarian |
| Gatherers | Miner, Botanist, Fisher |

Alternate sets retain their relative order within each job. Base classes follow their matching job; Arcanist follows Summoner. Unknown jobs remain at the end in their existing order. No duplicates are removed.

## Preservation and blockers

Sorting changes set numbers while preserving names, saved equipment, materia, dyes, glamour and portrait links, and display settings. Worn equipment stays unchanged. Existing missing-item warnings are not repaired by sorting.

The game's native reassignment operation updates gearset hotbar and Quick Panel references. Supported numeric gearset macro references are remapped to the same original set. Named macro references are checked against the game's prefix matching; an ambiguous or changed target blocks the sort. Unsupported macro syntax also blocks rather than guessing.

Macro reference handling currently supports the English game client. On other client languages, any nonempty saved macro blocks sorting and undo because localized gearset commands have not been validated. Preview and status remain available; clients without any saved macro text can still sort. This restriction is reported before any mutation.

A logged-in local player, ready native data, and idle affected automation are required. The preview identifies the current character, and every action is bound to that character. Combat, crafting, gathering, duty/area transitions, and relevant open editors can block the operation. The organizer does not stop or start automation. Errors and blockers appear in the window; resolve the reported condition and refresh the preview before continuing.

Each preview is tied to the current character, game process, gearsets, worn equipment, hotbars, and macros. Previews expire after ten minutes and are invalidated by plugin reload or a newer preview. Gearset Organizer rechecks the complete state and idle conditions when a button is used; a stale window cannot authorize a changed plan.

## Undo and saved evidence

Undo is available only for the latest successful sort that actually moved sets or changed references. A no-op sort does not replace that record. Undo requires the same character and running game process, with gearsets, worn equipment, hotbars, Quick Panel state, and macros matching the retained result. Any intervening change can make undo unavailable.

The undo record survives a plugin reload within the same game process. It does not carry authorization across a game restart. Preview tokens are temporary even when an undo record exists. Sorting and undo use the game's normal save behavior; native verification and completed disk saves are separate checks.

Before/after snapshots and journals are retained in Dalamud's Gearset Organizer configuration directory, under `gearsets/<character-content-id>/runs/<run-id>/`. The latest undo reference is stored at `gearsets/<character-content-id>/latest-undo.json`. Character IDs are written in hexadecimal. These files contain character, equipment, hotbar, and macro data; review them before sharing diagnostics.

Once an undo starts, its token cannot be replayed. A failed or uncertain operation requires journal/current-state reconciliation; repeatedly clicking or copying backup game files over a running client is not recovery.

## Optional external access

Gearset Organizer provides the `GearsetOrganizer.Request` IPC endpoint for external clients. The plugin's own window calls its native engine directly. Installing or disabling an external client does not change access to preview, sort, or undo. IPC requests use the same character, process, freshness, state, and idle checks as the window.

## Building and testing

The project targets .NET 10 for Windows and Dalamud API 15. Dalamud supplies its own managed libraries and native client structures at runtime; these host libraries must not be included in the plugin package.

Install the .NET 10 SDK and obtain matching Dalamud development libraries. The source and tests are included in [source](source/). From that directory:

```powershell
dotnet build ./GearsetOrganizer/GearsetOrganizer.csproj -c Release --nologo -warnaserror
dotnet test ./GearsetOrganizer.Tests/GearsetOrganizer.Tests.csproj -c Release --nologo
pwsh -NoProfile -File ./GearsetOrganizer.Tests/Verify-StandalonePackage.ps1
```

Add `-p:DalamudLibPath=C:\path\to\Dalamud\` to the build command when the matching Dalamud development libraries are stored outside the default XIVLauncher profile. DalamudPackager produces `GearsetOrganizer/bin/Release/net10.0-windows/GearsetOrganizer/latest.zip`. The package verification script checks the embedded sorting engine, allowed assembly references, complete runtime payload, matching manifest, and package hashes.

For in-game development, add the built DLL to Dalamud's **Dev Plugin Locations** and enable the plugin. Keep its manifest and runtime files beside the DLL. Open `/gearsets` to exercise preview, sorting, and undo. Unit tests and package verification do not replace a live sort/undo check.

Automation guards use status IPC and supplementary read-only adapters only for supported plugins that are loaded. They introduce no installation dependencies. Some adapters inspect third-party internal state; incompatible updates can block sorting until the adapter is updated. Test those integrations separately when their versions change.
