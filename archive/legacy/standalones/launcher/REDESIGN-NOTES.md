# Aurora 21 (Alpha v1) launcher redesign

This version keeps the existing Aurora 21 (Alpha v1) launcher workflows and replaces the presentation layer with a Collapse-inspired game-launcher interface.

## Preserved functionality

- FIFA 21 launch and stop workflows
- Discord / WireGuard player access workflow
- Python 3.12 prerequisite setup
- Runtime status polling
- FIFA21.exe browsing and per-user saved settings
- Existing PowerShell scripts, runtime-source files, auth bridge and server configuration

## UI changes

- Compact left navigation rail instead of a wide utility-style sidebar
- Full-width FIFA 21 hero panel using the existing Aurora 21 (Alpha v1) artwork
- Large PLAY action and cleaner STOP action
- Integrated connection and game state pills in the hero
- Modern status cards for connection and runtime details
- Cleaner settings screen with artwork treatment
- Dark layered surfaces, subtle borders and a purple Aurora 21 (Alpha v1) accent system
- Slow animated hero artwork zoom
- Fade/slide page transitions when switching Play / Settings
- Hover and pressed scaling on navigation and action buttons

The layout is inspired by the presentation language of modern launchers such as Collapse, but it does not include Collapse branding or copied assets.

## Build

On the Windows development machine, run:

`BUILD-REDESIGNED-LAUNCHER.cmd`

or:

`powershell -ExecutionPolicy Bypass -File .\Build-FIFA21-Launcher.ps1 -LauncherOnly`

The build script outputs the refreshed `Aurora21-Launcher.exe` and companion .NET files into this folder.
