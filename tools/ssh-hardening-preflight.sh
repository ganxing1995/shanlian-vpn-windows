#!/usr/bin/env bash
set -u

TARGET_USER="${TARGET_USER:-linuxuser}"
FALLBACK_PORTS="${FALLBACK_PORTS:-8443 2053 2087 2096 2083}"

bool() {
  if "$@"; then
    printf 'True'
  else
    printf 'False'
  fi
}

have() {
  command -v "$1" >/dev/null 2>&1
}

detect_ssh_service() {
  if have systemctl; then
    if systemctl list-unit-files ssh.service >/dev/null 2>&1 || systemctl status ssh >/dev/null 2>&1; then
      printf 'ssh'
      return
    fi
    if systemctl list-unit-files sshd.service >/dev/null 2>&1 || systemctl status sshd >/dev/null 2>&1; then
      printf 'sshd'
      return
    fi
  fi
  if service ssh status >/dev/null 2>&1; then
    printf 'ssh'
  elif service sshd status >/dev/null 2>&1; then
    printf 'sshd'
  else
    printf 'unknown'
  fi
}

detect_current_ssh_ports() {
  local ports
  ports="$(sshd -T 2>/dev/null | awk '$1=="port"{print $2}' | sort -n | uniq | xargs 2>/dev/null || true)"
  if [ -z "$ports" ] && [ -f /etc/ssh/sshd_config ]; then
    ports="$(awk 'tolower($1)=="port"{print $2}' /etc/ssh/sshd_config | sort -n | uniq | xargs 2>/dev/null || true)"
  fi
  if [ -z "$ports" ]; then
    ports="22"
  fi
  printf '%s' "$ports"
}

detect_public_ip() {
  local ip
  ip="$(curl --noproxy '*' -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)"
  if [ -z "$ip" ]; then
    ip="$(curl --noproxy '*' -fsS --max-time 5 https://ifconfig.me 2>/dev/null || true)"
  fi
  if [ -z "$ip" ]; then
    ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  fi
  printf '%s' "${ip:-unknown}"
}

user_exists() {
  id "$TARGET_USER" >/dev/null 2>&1
}

home_dir() {
  getent passwd "$TARGET_USER" 2>/dev/null | cut -d: -f6
}

mode_of() {
  stat -c '%a' "$1" 2>/dev/null || printf 'missing'
}

owner_of() {
  stat -c '%U:%G' "$1" 2>/dev/null || printf 'missing'
}

linuxuser_sudo() {
  id -nG "$TARGET_USER" 2>/dev/null | tr ' ' '\n' | grep -Eq '^(sudo|wheel)$'
}

authorized_keys_ok() {
  local home
  home="$(home_dir)"
  [ -n "$home" ] \
    && [ -d "$home/.ssh" ] \
    && [ -f "$home/.ssh/authorized_keys" ] \
    && [ "$(mode_of "$home/.ssh")" = "700" ] \
    && [ "$(mode_of "$home/.ssh/authorized_keys")" = "600" ] \
    && [ "$(owner_of "$home/.ssh")" = "$TARGET_USER:$TARGET_USER" ] \
    && [ "$(owner_of "$home/.ssh/authorized_keys")" = "$TARGET_USER:$TARGET_USER" ] \
    && [ -s "$home/.ssh/authorized_keys" ]
}

print_section() {
  printf '\n== %s ==\n' "$1"
}

ssh_service="$(detect_ssh_service)"
current_ports="$(detect_current_ssh_ports)"
public_ip="$(detect_public_ip)"
home="$(home_dir)"

print_section "SSH hardening preflight"
printf 'current_user=%s\n' "$(id -un 2>/dev/null || whoami)"
printf 'is_root=%s\n' "$(bool test "$(id -u)" -eq 0)"
printf 'ssh_service=%s\n' "$ssh_service"
printf 'current_ssh_port=%s\n' "$current_ports"
printf 'server_public_ip=%s\n' "$public_ip"
printf 'linuxuser_exists=%s\n' "$(bool user_exists)"
printf 'linuxuser_home=%s\n' "${home:-missing}"
printf 'linuxuser_home_mode=%s\n' "$(mode_of "${home:-/nonexistent}")"
printf 'linuxuser_ssh_dir_mode=%s\n' "$(mode_of "${home:-/nonexistent}/.ssh")"
printf 'linuxuser_ssh_dir_owner=%s\n' "$(owner_of "${home:-/nonexistent}/.ssh")"
printf 'authorized_keys_exists=%s\n' "$(bool test -f "${home:-/nonexistent}/.ssh/authorized_keys")"
printf 'authorized_keys_mode=%s\n' "$(mode_of "${home:-/nonexistent}/.ssh/authorized_keys")"
printf 'authorized_keys_owner=%s\n' "$(owner_of "${home:-/nonexistent}/.ssh/authorized_keys")"
printf 'authorized_keys_ok=%s\n' "$(bool authorized_keys_ok)"
printf 'linuxuser_sudo=%s\n' "$(bool linuxuser_sudo)"

print_section "Firewall"
if have ufw; then
  ufw status verbose 2>/dev/null || true
else
  printf 'ufw=missing\n'
fi
if have nft; then
  printf '\n-- nft ruleset summary --\n'
  nft list ruleset 2>/dev/null | sed -n '1,120p' || true
fi
if have iptables; then
  printf '\n-- iptables INPUT summary --\n'
  iptables -S INPUT 2>/dev/null || true
fi

print_section "Listening ports"
if have ss; then
  ss -tulpn 2>/dev/null || true
else
  printf 'ss=missing\n'
fi

print_section "Hysteria2 services"
if have systemctl; then
  systemctl list-units --type=service --all 2>/dev/null | grep -Ei 'hysteria|hysteria2|hy2|sing-box' || true
  for svc in hysteria-server hysteria hysteria2 sing-box; do
    systemctl status "$svc" --no-pager -l 2>/dev/null | sed -n '1,20p' || true
  done
fi
printf 'hysteria_candidate_ports=443 %s\n' "$FALLBACK_PORTS"

print_section "Report"
printf 'linuxuser_exists=%s\n' "$(bool user_exists)"
printf 'authorized_keys_ok=%s\n' "$(bool authorized_keys_ok)"
printf 'linuxuser_sudo=%s\n' "$(bool linuxuser_sudo)"
printf 'current_ssh_port=%s\n' "$current_ports"
printf 'new_ssh_port=\n'
printf 'sshd_config_test=False\n'
printf 'firewall_ok=False\n'
printf 'root_password_login_disabled=False\n'
printf 'password_login_disabled=False\n'
printf 'linuxuser_key_login_verified=False\n'
printf 'hysteria_ports_preserved=Unknown\n'
printf 'lockout_risk=%s\n' "$(authorized_keys_ok && linuxuser_sudo && printf 'medium' || printf 'high')"
