# Aurora 21 (Alpha v1) player launcher

This folder is the Windows player package. It connects FIFA 21 to the private
Aurora 21 (Alpha v1) server. It does not host the shared server and contains no server
administration, card-grant, club-editing, reset, or VPS-log controls.

## What the player needs

- Windows 10 or Windows 11 on a PC that can run FIFA 21
- FIFA 21 installed through the EA App
- [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
- A Discord account approved by a Aurora 21 (Alpha v1) administrator
- Permission to approve one Windows administrator prompt on first setup

## First launch

1. Extract the entire folder. Do not move individual files out of it.
2. Double-click `RUN-FIFA21.cmd`. The launcher automatically checks for its
   pinned Python 3.12 runtime and required secure-networking component. If they
   are missing, it downloads the official Python Software Foundation-signed
   installer and installs them for the current Windows user. Private local
   connection certificates are generated uniquely on that PC rather than being
   shipped inside the ZIP.
3. Open **Settings**, select the real installed `FIFA21.exe`, and choose **Save**.
4. Choose **Connect Discord**. Complete the single Discord approval in the
   browser window.
5. Accept the Windows administrator prompt. The launcher downloads the official
   signed WireGuard client, creates this device's private key locally, and
   prepares the secure connection.
6. If the launcher says approval is pending, ask a Aurora 21 (Alpha v1) administrator to
   approve your Discord account, then choose **Check Again**.
7. Choose **Launch FIFA 21**.

After first setup, normally only `RUN-FIFA21.cmd` and **Launch FIFA 21** are
needed. WireGuard itself has no player username or password. The protected
Discord device token and WireGuard private key remain on that Windows account.

## If something fails

- **Python setup failed:** check the internet connection, close and reopen the
  launcher, and allow downloads from `www.python.org` and `pypi.org`.
- **FIFA21.exe not found:** use Settings and select the game executable, not the
  FIFA configuration program.
- **Pending approval:** the Discord account has connected successfully but an
  administrator must approve it.
- **Connection unavailable:** choose **Connect Discord** again. If it still
  fails, send the administrator the exact message displayed in the launcher.
- **Game is already open:** close FIFA 21, choose **Stop**, and launch again.

Do not share the device-token or WireGuard configuration stored under
`%LOCALAPPDATA%\Project21`. A ban or approval removal revokes the device and
removes its WireGuard peer from the VPS; reinstalling the launcher does not
bypass a Discord ban.

## Launcher media folder

The launcher reads optional presentation media from the `sources` folder beside `Aurora21-Launcher.exe`.

- Put the hero background video at `sources\background.mp4` (recommended H.264 MP4). `hero.mp4` and `launcher.mp4` are also preferred names; otherwise the first supported video in the folder is used.
- Put one or more `.mp3` files in `sources`. Music starts automatically, plays the files alphabetically, and loops through the playlist.
- The title bar player intentionally shows no track names. It only exposes play/pause, mute, and volume.
- If no compatible hero video is found, the launcher falls back to the bundled FIFA artwork.
