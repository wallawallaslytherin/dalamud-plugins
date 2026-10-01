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

Enable **Disable when another player is present** to refuse activation or unlock
when any other player is detected in the house, with no distance filter. This
option starts off after every reload and can change while locked. The lock does
not resume automatically after other players leave. Detection uses the entire
client player-object list; players not reported by the game cannot be detected.

Enable **Auto-enable in housing placement mode** to lock when you enter the
native housing placement mode. This option is off initially; your choice is saved.
Turning it on while already placing attempts activation once. An enabled saved
choice also activates when loading into existing placement mode. Leaving placement mode or
turning the option off unlocks only a lock created by this option. Manual locks
are preserved. Manual unlocks, blocked activation and killswitch trips never
retry until a fresh placement-mode entry. All housing and player-presence gates
still apply. It reads the game's native placement state and requires no placement plugin.
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

Collapse or expand the window using its title bar. Collapsing it leaves the lock
and safety checks running.

Requires Dalamud API 15 and .NET 10.

Version 1.5.0.0 passes 3,210 safety checks, position-lease tests and twelve
placement-transition checks. Native placement-triggered locking was verified
in game. Full movement/input, multiplayer and exit-recovery acceptance remain
incomplete.

