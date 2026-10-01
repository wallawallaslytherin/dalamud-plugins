# Z-Lock

Lift your character 1 cm and stay in place inside player housing.

Open `/zlock` and press **Lock Height & Movement**. You rise 1 cm above your
current standing height, once, then stay locked there. Press **Unlock** to
return to normal movement. Height and horizontal position are locked together;
movement and jumping are blocked while locked. You
can still use the camera and plugin button. The button
is disabled outside a loaded house interior. While locked, temporary footing
lets the game use normal standing movement. Player apartments and private
chambers count as housing interiors; inns, outdoor wards, duties and workshops
do not.

Optionally enable **Allow movement** before locking to move horizontally while
height stays locked. **WARNING: Use at your own risk!** Unlock before changing the option.
It starts off after every plugin reload and is not saved. The same 1 cm lift and
housing/session safety gates apply in either mode.

The lock starts off and is never saved. Leaving the interior, changing zones,
logging out, entering GPose, mounting, entering combat or losing the current
player or being moved by the game immediately clears it. Entering another house never relocks it. Closing
the window leaves the lock active until unlocked or the housing session ends.

The lift is a fixed 0.01 game units along the vertical axis. It does not repeat
while locked, and it does not snap you back down on unlock; normal gravity resumes.
If activated while already airborne, it raises your current height by the same
small amount. It has no adjustable height control. `/zlock status` reports the state, and
`/zlock test` runs isolated checks without moving your character.

Unlock another active position lock before using this one. Only one position
writer can hold the lock in the current game process.

Requires Dalamud API 15 and .NET 10.

Version 1.2.0.0 passes 3,196 isolated safety checks and position-lease tests.
Gameplay movement, furniture traversal and warning appearance await in-game
acceptance. The warning is always shown at the bottom in larger bold red text.

