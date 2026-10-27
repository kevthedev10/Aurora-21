# Panel image assets

Downloaded card frames, player portraits, dynamic portraits, club badges, league badges and nation flags live here.
This folder was moved from `%LOCALAPPDATA%\FIFA21UltimateTeam\panel-assets` on 2026-09-30; club state and settings
remain in their existing user directories.

`ultimateteam/download-card-images.py` writes here by default. `Start-Panel-Review.ps1` passes this path explicitly;
the server also prefers this folder beside `runtime-source` when it exists. Hosted deployments can still use
`--panel-assets-dir` with their mounted image folder.

The Champions frame `cards/hd/18_champions_gold.png` uses the supplied `fifa21_champions_red_fixed_2.png`;
`cards/hd/10_pro_player.png` uses the supplied `fifa21_pro_player_fixed.png`. Both are also embedded as server fallbacks.
Downloader-generated `missing.txt` reports are ignored by Git; the image files are not ignored.
