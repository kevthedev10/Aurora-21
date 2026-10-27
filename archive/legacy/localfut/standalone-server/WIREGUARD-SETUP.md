# WireGuard checklist

1. Run `sudo bash ubuntu/install.sh` and `sudo bash ubuntu/configure.sh` on the
   Ubuntu VPS.
2. Allow the selected WireGuard UDP port in the VPS provider firewall.
3. Copy the printed endpoint and server public key into
   `cloudflare-control-panel/wrangler.jsonc`.
4. Apply the D1 migrations and deploy the Worker.
5. Put the deployed Worker URL in both `standalone-server/control-plane-host.json`
   and `standalone-launcher/control-plane.json`.
6. Run `sudo bash ubuntu/status.sh` and confirm `wg0`, the gateway and the host
   agent are active.
7. On a separate Windows computer, run the standalone launcher and choose
   **Connect Discord**. Approve Discord once and accept the Windows administrator
   prompt that installs WireGuard.
8. Approve the Discord user in the web panel, then use **Check Connection**.

Players do not create WireGuard accounts or log in to WireGuard. Each launcher
generates a device key automatically. Removing approval or banning the Discord ID
removes that device's secure network access.
