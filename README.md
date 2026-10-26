# 112 Operator Accessibility

Screen-reader accessibility mod for the 64-bit Windows Steam version of **112 Operator**. Release 0.0.23 uses BepInEx 5 x64 and the official x64 NVDA Controller Client.

The mod is playable in Free Game and Career Mode. Full duties, incoming calls, direct dispatch, multi-scene incidents, unit transport tasks, and duty completion have been verified.

## Controls

- `Up Arrow` / `Down Arrow`: browse active Unity controls
- `Enter` / `Numpad Enter`: activate the selected control
- `Space`: native game play/pause control, with spoken pause/resume feedback
- `1`, `2`, `3`: native game-speed controls
- `F1`: repeat the current control
- `F2`: browse active units
- `F3`: browse active incidents
- `F4`: browse starting districts
- `F5`: select the browsed starting district
- `F6`: open the newest incident alert
- `F7`: answer an incoming call
- `F8`: dispatch the browsed unit to the browsed incident
- `F9`: read the overall duty summary on the results screen
- `Shift+F2`, `Shift+F3`, `Shift+F4`: browse the corresponding lists backward

The player chooses both the unit and incident. The mod does not dispatch automatically.

## Spoken feedback

The mod provides NVDA speech and braille messages for menus, calls and their response choices, units, localized incident names, new incidents, unit task/status changes, scene additions and resolutions, and completed incidents.

When the starting-area screen opens, the mod explains that F4 browses districts and F5 selects one. During an answered call, menu navigation is limited to the active call choices and End Call control.

## Installation layout

The compiled plugin and NVDA client belong under:

`112 Operator/BepInEx/plugins/Operator112Accessibility/`

Launch the game normally through Steam with NVDA running.

## Known limitations

- The city map itself remains visual.
- Some management screens expose large amounts of text and may still have awkward labels.
- Starting-district selection uses the dedicated F4/F5 controls.
- Unknown task types from future game content are converted from their internal identifier into spaced, readable words, but may not be perfectly described.

The source intentionally remains under the 911 Operator installation folder for now. Do not move it until the known-good release baseline has passed regression testing and source control is initialized.
