# Windows development proxy guidance

The Windows client can run in a China development environment where Codex,
ChatGPT, or browsers use a local proxy. A local HTTP/SOCKS listener is allowed
as long as it does not take over TUN, DNS, or the default route.

Recommended setup:

- Clash, v2rayN, mihomo, or similar tools may keep local HTTP/SOCKS ports open.
- Keep TUN mode disabled in those tools while testing Shanlian VPN.
- Avoid running two VPN/TUN clients at the same time.
- Prefer per-process `HTTP_PROXY` / `HTTPS_PROXY` for development tools instead
  of enabling a global system proxy.
- If system proxy is enabled, the QA scripts use `curl --noproxy "*"` for
  direct checks so local development proxy state does not fake VPN success.

Blocked setup:

- Another Wintun, TUN, TAP, WireGuard, WARP, OpenVPN, Tailscale, AnyConnect, or
  similar adapter is up and owns VPN routes.
- The default route points at another VPN/TUN adapter.
- DNS is routed through another VPN/TUN adapter.
- Another non-Shanlian sing-box instance is running as a VPN/TUN service.
