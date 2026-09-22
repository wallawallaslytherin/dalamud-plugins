# Dalamud plugins

Custom Dalamud plugin repository maintained by [wallawallaslytherin](https://github.com/wallawallaslytherin).

## Install

1. In FFXIV with Dalamud loaded, enter `/xlsettings`.
2. Open **Experimental**, then **Custom Plugin Repositories**.
3. Paste this URL, add it, enable the entry, and save:

   ```text
   https://raw.githubusercontent.com/wallawallaslytherin/dalamud-plugins/main/pluginmaster.json
   ```

4. Open `/xlplugins`, find **Hovercast** or **Gearset Organizer** under **All Plugins**, and install it.

Keep the repository enabled to receive updates through Dalamud's plugin installer.
Regular Dalamud is sufficient; no custom runtime or developer-plugin setup is required.

## Plugins

| Plugin | Version | Description |
| --- | --- | --- |
| [Hovercast](plugins/FrameMouseover/README.md) | 0.2.2.0 | Automatic friendly mouseover, strict recipient protection, modifier overrides and optional ground placement. No spell lists or macros. |
| [Gearset Organizer](plugins/GearsetOrganizer/README.md) | 0.1.0.0 | Preview and sort saved gearsets by role and job, preserve their references, and undo the last sort. |

This repository distributes plugin packages, usage documentation, available source, and the installer manifest. It is public
and separate from Dalamud's default plugin repository.

## Updates

Hovercast uses `/hovercast` to open settings. The persisted internal ID is
`FrameMouseover`, so existing installations receive normal updates without a
reinstall or loss of settings. This ID is not user-facing branding.

## Repository layout

- `pluginmaster.json`: the URL to add to Dalamud; lists current plugin versions.
- `plugins/<InternalName>/<version>/`: versioned install ZIPs and SHA-256 checksums.
- `plugins/<InternalName>/README.md`: usage documentation for each plugin.
- `plugins/<InternalName>/source/`: source and tests, where included.

Each install ZIP contains the DLL, matching plugin manifest and runtime dependency
metadata at the archive root. Dalamud itself supplies the SDK assemblies.

