# Frame Mouseover

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
- Hostile actions, explicit macros and ground-placement actions keep their normal behavior.
- Range, line of sight, MP, cooldowns and other normal game restrictions apply.
- With no eligible frame mouseover, normal targeting is preserved.

Custom unit-frame plugins must publish the game's native UI mouseover target to
work with Frame Mouseover. Another plugin that changes action targeting can affect
the final recipient.

## Commands

| Command | Effect |
| --- | --- |
| `/framemouseover` | Show current status. |
| `/framemouseover status` | Show current status. |
| `/framemouseover test` | Run read-only runtime diagnostics. |
| `/framemouseover on` | Enable mouseover casting. |
| `/framemouseover off` | Disable mouseover casting. |

## Installation and requirements

Follow the [repository installation instructions](../../README.md).
Requires Dalamud API 15. No other plugin is required.

## Version 0.1.0.0

Initial release. Automatic friendly-action discovery, native UI mouseover targeting,
normal action queue preservation, and status/enable/disable commands.
