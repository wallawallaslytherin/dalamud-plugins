# Z-Lock

Lift your character 1 cm and hold that position inside player housing.

Open `/zlock` and press **Lock Height & Movement**. Press **Unlock** to
return to normal movement. Height and horizontal position are locked together;
movement and jumping are blocked while locked. You
can still use the camera and plugin button. The button
is disabled outside a loaded house interior. While locked, temporary footing
lets the game use normal standing movement. Player apartments and private
chambers count as housing interiors; inns, outdoor wards, duties and workshops
do not.

The lock starts off and is never saved. Leaving the interior, changing zones,
logging out, entering GPose, mounting, entering combat or losing the current
player or being moved by the game immediately clears it. Entering another house never relocks it. Closing
the window leaves the lock active until unlocked or the housing session ends.

Activation raises your current position by 1 cm, including when already airborne.
Unlocking resumes normal gravity. There is no height slider.
`/zlock status` reports the state, and
`/zlock test` runs isolated checks without moving your character.

Unlock another active position lock before using this one. Only one position
writer can hold the lock in the current game process.

Requires Dalamud API 15 and .NET 10. Dalamud supplies all runtime dependencies.

## Validation

Version 1.1.0.0 passes 2,246 isolated housing, session, coordinate and native
support geometry checks, plus exclusive position-lease and disposal tests.
Native bindings and temporary collision self-checks have loaded successfully.
Floated-furniture interaction, stationary animation, keyboard/gamepad/mouse/autorun
and jump blocking, unlock recovery and automatic unlock on a real house exit
await gameplay checks.
