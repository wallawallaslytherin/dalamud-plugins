# Z-Lock

Hold your character's current vertical position inside player housing.

Open `/zlock` and press **Lock Height**. Press **Unlock Height** to
return to normal movement. Horizontal movement remains available. The button
is disabled outside a loaded house interior. While locked, temporary footing
lets the game use normal standing and running movement. Player apartments and private
chambers count as housing interiors; inns, outdoor wards, duties and workshops
do not.

The lock starts off and is never saved. Leaving the interior, changing zones,
logging out, entering GPose, mounting, entering combat or losing the current
player immediately clears it. Entering another house never relocks it. Closing
the window leaves the lock active until unlocked or the housing session ends.

The plugin captures your current height; it has no height slider or teleport
controls. `/zlock status` reports the state, and
`/zlock test` runs isolated checks without moving your character.

Requires Dalamud API 15 and .NET 10.
Dalamud supplies all runtime dependencies.

## Validation

The initial version passes 2,167 isolated checks and native collision checks.
Running animations while locked and automatic unlock on a real house exit
remain unconfirmed in gameplay.
