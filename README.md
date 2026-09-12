# Dalamud plugins

Custom Dalamud plugin repository maintained by [wallawallaslytherin](https://github.com/wallawallaslytherin).

## Install

1. In FFXIV with Dalamud loaded, enter `/xlsettings`.
2. Open **Experimental**, then **Custom Plugin Repositories**.
3. Paste this URL, add it, enable the entry, and save:

   ```text
   https://raw.githubusercontent.com/wallawallaslytherin/dalamud-plugins/main/pluginmaster.json
   ```

4. Open `/xlplugins`, find **Frame Mouseover** under **All Plugins**, and install it.

Keep the repository enabled to receive updates through Dalamud's plugin installer.
Regular Dalamud is sufficient; no custom runtime or developer-plugin setup is required.

## Available plugins

| Plugin | Version | Description |
| --- | --- | --- |
| [Frame Mouseover](plugins/FrameMouseover/README.md) | 0.1.0.0 | Automatically cast eligible friendly actions on native unit-frame mouseover targets. No spell lists or macros. |

This repository distributes plugin packages and the installer manifest. It is public
and separate from Dalamud's default plugin repository.

## Repository layout

- `pluginmaster.json`: the URL to add to Dalamud; lists current plugin versions.
- `plugins/<InternalName>/<version>/`: versioned install ZIPs and SHA-256 checksums.
- `plugins/<InternalName>/README.md`: usage documentation for each plugin.

Each install ZIP contains the DLL, matching plugin manifest and runtime dependency
metadata at the archive root. Dalamud itself supplies the SDK assemblies.

For updates, publish a new versioned ZIP before updating its entry in
`pluginmaster.json`. Keep existing versioned downloads available.
