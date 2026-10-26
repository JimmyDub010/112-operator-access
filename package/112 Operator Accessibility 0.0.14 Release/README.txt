112 OPERATOR ACCESSIBILITY
Release 0.0.14 - August 26, 2026

An unofficial screen-reader accessibility mod for the 64-bit Windows Steam
version of 112 Operator.

The mod provides NVDA speech and braille output, keyboard menu access, unit and
incident browsing, localized incident names, direct player-controlled dispatch,
incoming-call access, incident and unit-status announcements, and multi-scene
incident progress feedback.

Free Game and Career Mode are playable. Full duties, incoming calls, dispatch,
multi-scene incidents, hospital transport, Career progression, and duty
completion have been tested successfully.


REQUIREMENTS

- Windows Steam edition of 112 Operator
- NVDA running before the game starts
- A 64-bit version of Windows


INSTALLATION

1. Close 112 Operator.
2. In Steam, open Library and locate 112 Operator.
3. Open Properties, choose Installed Files, then Browse.
4. Extract every file and folder from this ZIP directly into the folder that
   contains "Operator 112.exe".
5. Allow folders to merge if Windows asks.
6. Start NVDA.
7. Launch 112 Operator normally through Steam.

Do not extract the package into an extra subfolder. After installation,
winhttp.dll, doorstop_config.ini, README.txt, and the BepInEx folder should be
beside Operator 112.exe.

At startup, NVDA should announce: "112 Operator Accessibility is loaded."


CONTROLS

Up/Down Arrow   Browse active controls
Enter           Activate the selected control
Numpad Enter    Activate the selected control
Space           Native game play/pause control, announces Paused or Resumed
1               Native normal speed
2               Native fast speed
3               Native very fast speed
F1              Repeat the current control
F2              Browse active units
F3              Browse active incidents
F4              Browse starting districts
F5              Select the browsed starting district
F6              Open the newest incident alert
F7              Answer an incoming call
F8              Dispatch the browsed unit to the browsed incident

The player always chooses the unit and incident. The mod never dispatches units
automatically.


SPOKEN FEEDBACK

The mod announces:

- accessible menus, dialogs, and call choices;
- units, departments, human-readable status, and current task;
- localized incident names;
- new incident and incoming-call alerts;
- scene additions, scene resolutions, and completed incidents;
- observable unit changes such as patrol, incident response, and transport.
- starting-area instructions explaining the F4/F5 district controls;
- only active call choices and End Call while an answered call is open.


KNOWN LIMITATIONS

- The city map remains visual. Dedicated unit, incident, and dispatch controls
  avoid much of the map interaction but do not provide a spatial map.
- Some management screens expose large amounts of information and may have
  awkward or incomplete labels.
- Starting-district selection uses F4 and F5.
- Some unusual or future task types may receive a generic spoken description.
- The game's repeated radio acknowledgements can occur when units stand down or
  respond across districts; this is native game behavior.


TROUBLESHOOTING

If nothing speaks:

1. Confirm NVDA was running before starting the game.
2. Completely close and restart 112 Operator.
3. Press F1 on a menu.
4. Confirm these files exist:
   BepInEx\plugins\Operator112Accessibility\Operator112Accessibility.dll
   BepInEx\plugins\Operator112Accessibility\nvdaControllerClient.dll
5. Check BepInEx\LogOutput.log for "112 Operator Accessibility loaded" and
   recent "SPEAK:" entries.

When reporting a problem, include the screen, incident, or call; the exact keys
pressed; what NVDA announced; what was expected; whether it reproduces; and the
BepInEx\LogOutput.log file when possible.


REMOVAL

Close the game, then remove:

- BepInEx
- winhttp.dll
- doorstop_config.ini
- .doorstop_version
- README.txt

If another mod also uses BepInEx, remove only:

BepInEx\plugins\Operator112Accessibility

Removing the accessibility mod does not remove saved games.


LICENSES AND ACKNOWLEDGEMENTS

The official NVDA Controller Client is included under the GNU Lesser General
Public License. Its license text is included beside the client DLL at:

BepInEx\plugins\Operator112Accessibility\NVDA-controller-license.txt

BepInEx and its components retain their respective licenses. 112 Operator, its
code, names, and assets belong to their respective owners. This project is an
unofficial accessibility modification.
