# Character Copy

Install Character Copy through `/xlplugins` after adding this repository's feed in `/xlsettings` → Experimental → Custom Plugin Repositories.

Log into the character whose settings you want to copy. Open `/cc` and click **Back up and copy to all other characters**. Stay logged into that character until the operation finishes.

The plugin requests native settings saves, creates and verifies a ZIP backup of the entire local game settings directory, then copies every file and subfolder from the logged-in character to every other existing local character folder. This includes UISAVE.DAT, strategy-board data, gearsets, hotbars, macros and chat logs present in that folder. Matching destination files are overwritten; destination-only files and character-folder IDs are preserved. All existing character folders on the PC are included, even across accounts.

Backups are stored in the plugin's configuration directory under Backups. They may contain private chat history; keep them private. For full recovery, return to the title screen and use the plugin's configuration window or `/cc restore`. Recovery preserves the replaced settings directory alongside the restored one. Interrupted copies attempt automatic rollback.

Server-side inventory, progress and unlocks and separately stored plugin configuration are outside this transfer.

Requires Windows and Dalamud API 15. Version 1.0.0.0 passed build, synthetic file-copy/rollback/restore tests, live loading, native module identity checks and Dalamud validation. Native saving and the complete in-game transfer/recovery workflow remain untested; persistence of currently unsaved COMMON/CONTROL values is not verified.