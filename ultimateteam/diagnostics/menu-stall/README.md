# One-session menu timing capture

From the repository root in **Administrator Windows PowerShell**, with FIFA already
running and connected to the hosted server:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\ultimateteam\Capture-MenuStall.ps1 -ServerIp YOUR_SERVER_IPV4
```

The script does not elevate itself, restart anything, change networking or touch
club state. It uses Windows Packet Monitor, requiring elevation and no existing
Packet Monitor capture or filters. It fails closed when the status cannot be
recognized (including an unsupported localized status format).

Return to FIFA. Press **F8** just before entering/leaving the store, paging the
market or inspecting a player. Press **F9** when the screen finishes loading. Repeat
for a quick action and a slow action, then press **F10** to finish. The capture
automatically finishes after 10 minutes; `-Minutes 20` allows a longer idle period.
These keys are observed globally, so the console can stay behind the game. Only
those three keys are observed; they still reach the game, so avoid assigning them
to game/mod actions. The script does not collect screenshots, other keystrokes,
process memory, command lines, credentials or decrypted TLS traffic.

Share the printed `menu-stall-report.zip` path, and say which numbered action was
store/market/player inspection. The bundle contains UTC markers, TCP header timing,
all FIFA TCP socket destinations/states, sampled process CPU activity, capture tool
output and a report. Local file paths and endpoint addresses appear in it. Raw
header-limited ETL/pcapng files remain beside the bundle and are excluded from the
zip. All outputs default to the ignored `outputs/menu-stall/` directory.

The packet filter covers the selected server's TLS game ports 18443–18446 and
redirector 42230. The NIC capture is limited to 96 bytes per packet. The parser
exports only IPv4 TCP headers, lengths and timestamps; payload bytes never appear
in the shareable CSV. Only the capture started by this script is stopped, and its
filters are removed only if their listing has not changed since creation.

The packet timeline distinguishes a long gap before sending, repeated data on an
old connection, and opening a new connection. It does **not** decrypt request
paths, prove packet loss from repeated sequences, prove delivery to FIFA from a
TCP ACK, or identify the exact internal game wait. NIC offload and multiple NIC
capture points can duplicate/coalesce packets. Compare action markers, socket
ownership and packet flows together; pair the UTC window with server request logs
only if route identification remains necessary. `Established` alone is not proof
that an idle path is usable. A pre-send gap could involve an internal queue, DNS
or another service, rather than this server.

Offline verification (synthetic packets, no capture or game required):

```powershell
powershell.exe -NoProfile -File .\ultimateteam\diagnostics\menu-stall\Test-TraceReader.ps1
```

The first live capture still needs verification on Windows. No hosted fix is
implemented or accepted merely because this diagnostic compiles or its fixtures pass.
