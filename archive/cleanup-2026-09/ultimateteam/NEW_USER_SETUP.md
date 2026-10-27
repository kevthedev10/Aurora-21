# Aurora 21 UltimateTeam — New User Setup

## 1. Install the requirements

1. Install the **EA App**, sign in to the account that owns FIFA 21, and install FIFA 21.
2. Install the **.NET 10 SDK (x64)** from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The SDK includes the Desktop Runtime the launcher needs. Python is no longer required.
3. Open the extracted `ultimateteam` folder, then double-click:
   `runtime-source\INSTALL_PREREQUISITES.cmd`

This builds `Aurora21-UltimateTeam-Server.exe` (the local FUT server), refreshes the launcher, and downloads the portable PostgreSQL build that stores your club (about 25 MB, verified by checksum). The first build needs an internet connection; nothing else has to be installed. To check the SDK manually, open PowerShell and run:

```powershell
dotnet --list-sdks
```

## 2. Prepare the build

- Extract or copy the **entire build** into one folder. Do not combine a new launcher with scripts from an older build.
- Keep the included `runtime-source`, DLL, JSON, PowerShell, and certificate files beside the launcher.
- If `runtime-source\certs` is missing, run `runtime-source\GENERATE_LOCAL_CERT.cmd` once.
- Close FIFA 21 before the first launch.

## 3. Configure and launch FIFA 21

1. Start the EA App and make sure you are signed in.
2. In the `ultimateteam` folder, open **`Aurora21-UltimateTeam-Launcher.exe`**.
3. Open **Settings** and select the real installed **`FIFA21.exe`**. Do not select the FIFA configuration launcher.
4. Leave the runtime source on this build's bundled `runtime-source` folder and save the settings.
5. Return to **Overview** and click **LAUNCH LOCAL FUT**.
6. Approve the Windows administrator/UAC prompt. The launcher needs it for the local hosts entry and the running-game trust bridge.
7. Wait for the launcher to report that the local services are ready. FIFA 21 should then launch through the EA App automatically.

Always start the game from **Aurora21-UltimateTeam-Launcher.exe** when using UltimateTeam. Do not launch `FIFA21.exe` directly first.

## Quick troubleshooting

- **Server executable missing / .NET SDK not found:** install the .NET 10 SDK, then run `runtime-source\INSTALL_PREREQUISITES.cmd` again.
- **Connection not running:** close FIFA, use **STOP ALL** or **RESTART LOCAL FUT**, then launch again as administrator.
- **EA App does not open FIFA:** open the EA App first, confirm the account owns FIFA 21, and verify the game installation.
- **Wrong game path:** select the installed file named exactly `FIFA21.exe` in launcher Settings.
- **Club database (PostgreSQL) not found:** run `runtime-source\INSTALL_PREREQUISITES.cmd` again.
- Logs are stored in the build's `runtime` folder. The club database is a private PostgreSQL cluster under `%LOCALAPPDATA%\FIFA21UltimateTeam\postgres`, reachable only from this PC and started and stopped with UltimateTeam. A club from an older Python-based build (`%LOCALAPPDATA%\FIFA21UltimateTeam\data\ultimateteam.sqlite3`) remains on disk and is not imported.

## Current known bugs

- The stadium is invisible in Solo Draft matches and away Squad Battles matches.
- Icon players can display random/incorrect player names in Solo Draft.
