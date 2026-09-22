# Hovercast

Cast friendly spells and abilities on the character beneath your cursor in native
party, alliance, and other unit frames, without changing your selected target.

## Use

Hover the intended member's frame and press your normal spell keybind or bound
mouse button. The plugin automatically recognizes eligible actions; no spell
lists, per-job setup, or macros are required. It is enabled immediately after
installation and opens no startup window.

Keep the cursor over the frame when pressing the action. Moving the cursor to
click an ordinary hotbar icon ends that frame mouseover.

- Friendly heals, raises, buffs, shields and eligible movement actions use the
  hovered recipient when the game permits it.
- Self-only abilities still affect yourself; party-only abilities remain party-only.
- Queued actions keep the recipient chosen when you pressed the action.
- Invalid friendly mouseovers are blocked by default, preventing accidental
  fallback to yourself or a different selected recipient. Strict mode is configurable.
- Optional held modifiers force self-cast or bypass mouseover for that press.
- Optional ground placement uses the hovered ally's feet, retains queueing, and
  rechecks the recipient before execution. It starts disabled.
- Hostile actions and explicit macros keep their normal behavior.
- Range, line of sight, MP, cooldowns and other normal game restrictions apply.
- With no friendly frame mouseover, normal targeting is preserved.

Custom unit-frame plugins must publish the game's native UI mouseover target to
work with Hovercast. Another plugin that changes action targeting can affect
the final recipient.

## Commands

| Command | Effect |
| --- | --- |
| `/hovercast` | Open settings. |
| `/hovercast status` | Show current status. |
| `/hovercast config` or `/hovercast settings` | Open settings and recent cast history. |
| `/hovercast test` | Run 13 routing simulations and live dependency checks; casts no spells. |
| `/hovercast history` | Show recent relevant casts and rejection reasons. |
| `/hovercast on` or `recover` | Check dependencies, clear suspension and enable mouseover. |
| `/hovercast off` | Disable mouseover casting. |
| `/hovercast strict on` or `strict off` | Block invalid friendly mouseovers, or allow normal targeting fallback. |
| `/hovercast ground on` or `ground off` | Enable or disable ground placement at hovered allies. |
| `/hovercast bind self alt` | Example: hold Alt to cast on yourself when supported. |
| `/hovercast bind bypass ctrl` | Example: hold Ctrl to use normal game targeting. |
| `/hovercast bind ground shift` | Example: require Shift for automatic ground placement. |

Modifier assignments start unset. Choose distinct Shift/Ctrl/Alt combinations,
such as `ctrl+shift`, or `none` to clear one. Combinations match exactly; your
modified hotbar keybind must still invoke the intended action. With ground
placement enabled, an unset ground modifier applies it whenever a friendly frame
is hovered. If automatic placement is cancelled, use the native reticle manually
or press Escape.

A routing fault suspends mouseover and gives one notice. Status and settings
show the fault; `on` and `recover` both run checks before restoring it.
History retains 64 relevant attempts; damage spam does not push them out. Queue
acceptance and observed client execution are distinguished, but neither proves
a server-applied heal or effect.

## Installation and requirements

Follow the [repository installation instructions](../../README.md).
Requires Dalamud API 15. No other plugin is required.

## Version 0.2.2.0

Hovercast uses `/hovercast` to open settings. The plugin name, UI, help and docs
use Hovercast consistently, and `/hovercast` is the only registered command.
Existing settings and the normal update path are preserved; no reinstall is
needed. Casting behavior is unchanged.

## Version 0.2.1.0

Fix ground placement stopping after the native reticle opens. Queued ground
actions retain their captured recipient; unrelated existing reticles are left
alone. Range errors now include distance and the action's range. Real range
restrictions still apply.

## Version 0.2.0.0

Strict invalid-hover protection; checked suspension recovery; self/bypass
modifiers; relevant cast history and routing dry runs; opt-in ground placement.
No spell lists or per-job setup are introduced. Settings stay closed on startup.

## Version 0.1.0.0

Initial release. Automatic friendly-action discovery, native UI mouseover targeting,
normal action queue preservation, and status/enable/disable commands.
