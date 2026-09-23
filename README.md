# Dalamud plugins

Custom Dalamud plugin repository maintained by [wallawallaslytherin](https://github.com/wallawallaslytherin).

## Install

1. In FFXIV with Dalamud loaded, enter `/xlsettings`.
2. Open **Experimental**, then **Custom Plugin Repositories**.
3. Paste this URL, add it, enable the entry, and save:

   ```text
   https://raw.githubusercontent.com/wallawallaslytherin/dalamud-plugins/main/pluginmaster.json
   ```

4. Open `/xlplugins`, find **Hovercast**, **Gearset Organizer**, or **Airwave** under **All Plugins**, and install it.

Keep the repository enabled to receive updates through Dalamud's plugin installer.

## Plugins

| Plugin | Version | Description |
| --- | --- | --- |
| [Hovercast](plugins/FrameMouseover/README.md) | 0.2.3.0 | Automatic friendly mouseover, strict recipient protection, modifier overrides and optional ground placement. No spell lists or macros. |
| [Gearset Organizer](plugins/GearsetOrganizer/README.md) | 0.1.0.0 | Preview and sort saved gearsets by role and job, preserve their references, and undo the last sort. |
| [Airwave](plugins/Airwave/README.md) | 0.1.0.0 | Broadcast live rekordbox audio or join with a listener invite. Includes a one-click local sound test and bundled Windows audio helpers. |

This is a custom repository, separate from Dalamud's default plugin repository.

## Updates

Install updates through `/xlplugins`. Existing Hovercast installations receive
updates without a reinstall or loss of settings.

## Repository layout

- `pluginmaster.json`: the URL to add to Dalamud; lists current plugin versions.
- `plugins/<InternalName>/<version>/`: versioned install ZIPs and SHA-256 checksums.
- `plugins/<InternalName>/README.md`: usage documentation for each plugin.
- `plugins/<InternalName>/source/`: source and tests, where included.

Each install ZIP contains the DLL, matching plugin manifest and runtime dependency
metadata at the archive root. Dalamud itself supplies the SDK assemblies.
