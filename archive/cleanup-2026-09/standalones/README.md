# Project 21 control-plane source

The Python standalone launcher and game server previously stored here have been
moved to `archive/legacy/standalones/`. They are historical reference material,
not supported deployment components.

`server/control-plane/` remains because its account, device, approval, and owner
administration contracts may be reused by the C# server's eventual public edge
gateway. It is not currently part of the supported local C# runtime and must not
be deployed as though it were already integrated.

The supported launcher and server are under `localfut/`. See the
[repository README](../README.md) and [server README](../localfut/server/README.md)
for current entrypoints and verification commands.

Personal settings, device credentials, private keys, mutable databases, uploaded
media, build caches, dependencies, and VPS secrets remain excluded from Git.
