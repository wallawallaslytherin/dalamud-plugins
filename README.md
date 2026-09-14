# Dalamud plugins

Custom Dalamud plugin repository maintained by [wallawallaslytherin](https://github.com/wallawallaslytherin).

## Install

1. In FFXIV with Dalamud loaded, enter `/xlsettings`.
2. Open **Experimental**, then **Custom Plugin Repositories**.
3. Paste this URL, add it, enable the entry, and save:

   ```text
   https://raw.githubusercontent.com/wallawallaslytherin/dalamud-plugins/main/pluginmaster.json
   ```

4. Open `/xlplugins`, find **Hovercast** under **All Plugins**, and install it.

Keep the repository enabled to receive updates through Dalamud's plugin installer.
Regular Dalamud is sufficient; no custom runtime or developer-plugin setup is required.

## Plugins

| Plugin | Version | Description |
| --- | --- | --- |
| [Hovercast](plugins/FrameMouseover/README.md) | 0.2.2.0 | Automatic friendly mouseover, strict recipient protection, modifier overrides and optional ground placement. No spell lists or macros. |

This repository distributes plugin packages and the installer manifest. It is public
and separate from Dalamud's default plugin repository.

## Updates

Hovercast uses `/hovercast` to open settings. The persisted internal ID is
`FrameMouseover`, so existing installations receive normal updates without a
reinstall or loss of settings. This ID is not user-facing branding.

## Repository layout

- `pluginmaster.json`: the URL to add to Dalamud; lists current plugin versions.
- `plugins/<InternalName>/<version>/`: versioned install ZIPs and SHA-256 checksums.
- `plugins/<InternalName>/README.md`: usage documentation for each plugin.

Each install ZIP contains the DLL, matching plugin manifest and runtime dependency
metadata at the archive root. Dalamud itself supplies the SDK assemblies.

## Maintainer release policy

**Never push or publish anything without the owner's explicit instruction for
that specific update or release.** This includes documentation-only changes,
all remote branches, tags, draft/final releases, packages, and the installer feed.
Previous publication permission does not carry over to another update.

Develop and deploy to the local developer plugin first, then give the owner a
chance to test the actual behavior in game. Automated tests and dry runs are
separate from gameplay acceptance. "It works" confirms a test result; it does
not authorize a release. Keep small fixes local and batch them until the owner
requests publication. Do not ask to publish after every fix.

When explicitly instructed to publish, verify that the candidate matches the
tested build and authorized changes. Disclose and resolve any testing gaps
before publication. Make the new versioned ZIP available before directing
`pluginmaster.json` to it, and preserve existing downloads. Any later changes
require a new publication instruction.

## Maintainer naming policy

Use the chosen product name consistently in current UI, commands, help, scripts,
examples and docs. Remove obsolete names and compatibility aliases when renaming;
retain them only if the owner explicitly requests them. Verify live registrations
and search current source/docs before handing off a rename. Hidden persisted IDs
may remain where needed for settings and installed updates. Preserve historical
artifacts; naming cleanup does not authorize publication.
