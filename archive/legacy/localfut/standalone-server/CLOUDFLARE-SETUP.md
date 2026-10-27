# Cloudflare Tunnel + WARP setup

This mode keeps LocalFUT and all mutable state on the host PC. `cloudflared`
makes an outbound-only connection to Cloudflare; no router port forwarding or
public LocalFUT hostname is created. Players connect through the authenticated
Cloudflare One (WARP) client.

Windows 11 ARM64 VMs are supported through Windows x64 emulation. Cloudflare's
current Windows release is AMD64; the host preparation script detects ARM64,
checks the Cloudflare Authenticode signature, and verifies the connector can run
before installing its service.

## One-time Cloudflare dashboard setup

1. Create a Cloudflare Zero Trust organisation and note its team name (the part
   before `.cloudflareaccess.com`).
2. For the integrated Discord control panel, create a Cloudflare service token
   and allow that token in the WARP device-enrollment policy. Store its client ID
   and secret only as Worker secrets.
3. Create a remotely-managed Cloudflare Tunnel and copy its Windows connector
   token. Treat this token as a secret.
4. Run `Prepare-Cloudflare-Host.cmd` and supply the team name and tunnel token.
   It creates `10.203.21.1/32` on Windows' loopback interface automatically;
   no router access, DHCP reservation, or manual address selection is required.
5. In **Networking > Routes**, create a Tunnel CIDR route for
   `10.203.21.1/32`, and select the new tunnel.
6. In the WARP device profile, use split-tunnel **Include** mode for a
   VPN-replacement-only setup and include `10.203.21.1/32`. If your profile uses
   Exclude mode, remove the range containing the server address from exclusions.
7. In **Traffic policies > Traffic settings**, enable Gateway proxying for TCP
   and UDP.
8. Create a Gateway Network Allow policy limited to the routed server IP and
   LocalFUT ports, then a lower-priority catch-all Block policy for that server
   IP. Service-token-enrolled WARP clients appear to Cloudflare as `non_identity`,
   so do not depend on a Discord/user identity selector here. Cloudflare limits
   the reachable route and ports; the authenticated LocalFUT gateway validates
   the launcher's Discord-bound device token and enforces approval, revocation,
   and bans. Pilot this with one player before broadening enrollment.

LocalFUT ports: TCP `44321, 8090, 10853, 17502, 8081, 42231, 42232`; UDP
`17502` for QoS; and UDP `31000-31199` for the gameplay relay. Allow the entire
relay range in the Gateway Network policy and WARP route. Do not publish these
as public hostnames and do not forward them on the home router.

Current Cloudflare documentation:

- Private CIDR routing: https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/private-net/cloudflared/connect-cidr/
- WARP manual enrollment: https://developers.cloudflare.com/cloudflare-one/team-and-resources/devices/cloudflare-one-client/deployment/manual-deployment/

## Player experience

Deploy and configure `cloudflare-control-panel` first, then run
`Configure-Control-Plane-Host.cmd`. After host preparation, copy only the
`standalone-launcher` folder to a player. They open `RUN-FIFA21.cmd`, select
`FIFA21.exe`, and click Launch. First use asks for one Discord approval; WARP then
enrolls automatically without a separate Cloudflare login. The authenticated
gateway checks the device token on every connection and during active sessions.

## Rollback

Stop LocalFUT, uninstall the Cloudflare Tunnel Windows service with the
`cloudflared.exe` stored under `tools`, delete/disable the tunnel and its `/32`
route in Cloudflare, and set `cloudflare-client.json` `enabled` to `false` in
the player package. Run `Remove-Project21-StableAddress.cmd` as Administrator to
remove the dedicated Windows loopback address. Re-enable a LAN or Tailscale
firewall rule only if you are intentionally returning to that mode.
