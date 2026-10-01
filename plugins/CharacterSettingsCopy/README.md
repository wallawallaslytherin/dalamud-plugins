# Character Copy

Install Character Copy through `/xlplugins` after adding this repository's feed in `/xlsettings` → Experimental → Custom Plugin Repositories.

Log into the character whose settings you want to copy. Open `/cc`, check the source and destination count, then click **Copy to all other characters**. Stay logged into that character until the operation finishes.

The plugin requests native settings saves, creates and verifies a ZIP backup of the entire local game settings directory, then copies every file and subfolder from the logged-in character to every other existing local character folder. This includes UISAVE.DAT, strategy-board data, gearsets, hotbars, macros and chat logs present in that folder. Matching destination files are overwritten; destination-only files and character-folder IDs are preserved. All existing character folders on the PC are included, even across accounts.

**Undo last copy** restores overwritten destination files and removes files added by the latest successful copy. It preserves the source character, global settings and unrelated destination files. Use undo on the original source character or at the title screen. Undo stops if copied destination files have changed or are missing. Its record survives plugin reloads and is consumed on success; another successful copy replaces it. Copies made before version 1.1 have full-backup recovery only.

Backups are stored in the plugin's configuration directory under Backups. They may contain private chat history; keep them private. For full recovery, return to the title screen and expand **Full backup recovery**, use the plugin's configuration action or `/cc restore`. Full recovery replaces the entire settings directory after confirmation and preserves the displaced directory alongside it. Undo also preserves displaced target folders in a `.CharacterCopy-undo-…` directory beside the settings directory. Interrupted operations attempt automatic rollback.

Server-side inventory, progress and unlocks and separately stored plugin configuration are outside this transfer.

Requires Windows and Dalamud API 15. Version 1.1.0.0 passed build, 73 synthetic copy/undo/rollback/restore checks, live loading, native module identity checks and Dalamud validation. Visual UI acceptance and the complete in-game copy/undo/recovery workflow remain untested; persistence of currently unsaved COMMON/CONTROL values is not verified.
