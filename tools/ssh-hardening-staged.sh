#!/usr/bin/env bash
set -euo pipefail

TARGET_USER="${TARGET_USER:-linuxuser}"
PREFERRED_PORT="${PREFERRED_PORT:-28422}"
FALLBACK_PORTS="${FALLBACK_PORTS:-8443 2053 2087 2096 2083}"
STATE_FILE="${STATE_FILE:-/root/ssh-hardening-state.env}"
HARDENING_DROPIN="/etc/ssh/sshd_config.d/99-hardening.conf"
MANAGED_BEGIN="# BEGIN SHANLIAN SSH HARDENING"
MANAGED_END="# END SHANLIAN SSH HARDENING"

usage() {
  cat <<'USAGE'
Safe staged SSH hardening.

Usage:
  sudo bash tools/ssh-hardening-staged.sh --stage 0
  sudo bash tools/ssh-hardening-staged.sh --stage 1
  sudo bash tools/ssh-hardening-staged.sh --stage 2
  sudo bash tools/ssh-hardening-staged.sh --stage 3
  sudo bash tools/ssh-hardening-staged.sh --stage 4 --confirm-linuxuser-key-login
  sudo bash tools/ssh-hardening-staged.sh --stage 5
  sudo bash tools/ssh-hardening-staged.sh --stage 6 --confirm-new-port-login
  sudo bash tools/ssh-hardening-staged.sh --rollback

Stages:
  0  Preflight report only. No changes.
  1  Prepare linuxuser, sudo group, and authorized_keys permissions. No SSH policy changes.
  2  Print exact linuxuser login test command for a NEW terminal.
  3  Pick/open new SSH port in firewall. No sshd config changes.
  4  Apply safe sshd config only after linuxuser key login has been verified.
     Keeps old SSH port and new SSH port enabled.
  5  Restart SSH and print new-port test command. Do not close current session.
  6  Final lockdown only after new-port linuxuser key login is verified.
     Disables root login and removes old SSH firewall rules.

Environment:
  TARGET_USER=linuxuser
  PREFERRED_PORT=28422
  FALLBACK_PORTS="8443 2053 2087 2096 2083"
USAGE
}

die() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

have() {
  command -v "$1" >/dev/null 2>&1
}

require_root() {
  [ "$(id -u)" -eq 0 ] || die "Run with sudo or as root. Current session must stay open."
}

service_name() {
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
    printf 'ssh'
  fi
}

current_ports() {
  local ports
  ports="$(sshd -T 2>/dev/null | awk '$1=="port"{print $2}' | sort -n | uniq | xargs 2>/dev/null || true)"
  if [ -z "$ports" ] && [ -f /etc/ssh/sshd_config ]; then
    ports="$(awk 'tolower($1)=="port"{print $2}' /etc/ssh/sshd_config | sort -n | uniq | xargs 2>/dev/null || true)"
  fi
  printf '%s' "${ports:-22}"
}

public_ip() {
  local ip
  ip="$(curl --noproxy '*' -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)"
  [ -n "$ip" ] || ip="$(curl --noproxy '*' -fsS --max-time 5 https://ifconfig.me 2>/dev/null || true)"
  [ -n "$ip" ] || ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  printf '%s' "${ip:-SERVER_IP}"
}

supports_dropin() {
  [ -d /etc/ssh/sshd_config.d ] \
    && grep -Eiq '^[[:space:]]*Include[[:space:]]+/etc/ssh/sshd_config\.d/\*\.conf' /etc/ssh/sshd_config
}

choose_port() {
  local port="$PREFERRED_PORT"
  while ss -tuln 2>/dev/null | awk '{print $5}' | grep -Eq "[:.]${port}$"; do
    port=$((port + 1))
    if [ "$port" -gt 60999 ]; then
      die "Could not find an unused high SSH port"
    fi
  done
  printf '%s' "$port"
}

save_state() {
  local new_port="$1"
  local old_ports="$2"
  local backup="$3"
  umask 077
  cat >"$STATE_FILE" <<EOF
TARGET_USER='$TARGET_USER'
NEW_SSH_PORT='$new_port'
OLD_SSH_PORTS='$old_ports'
BACKUP_FILE='$backup'
SSH_SERVICE='$(service_name)'
EOF
}

load_state() {
  [ -f "$STATE_FILE" ] || die "Missing $STATE_FILE. Run stage 3 first."
  # shellcheck disable=SC1090
  . "$STATE_FILE"
  : "${NEW_SSH_PORT:?missing NEW_SSH_PORT in state}"
  : "${OLD_SSH_PORTS:?missing OLD_SSH_PORTS in state}"
  : "${BACKUP_FILE:?missing BACKUP_FILE in state}"
  : "${SSH_SERVICE:?missing SSH_SERVICE in state}"
}

validate_sshd() {
  if sshd -t; then
    printf 'sshd_config_test=True\n'
    return 0
  fi
  printf 'sshd_config_test=False\n'
  return 1
}

restart_ssh() {
  local svc="${1:-$(service_name)}"
  if have systemctl; then
    systemctl restart "$svc" || systemctl restart ssh || systemctl restart sshd
  else
    service "$svc" restart || service ssh restart || service sshd restart
  fi
}

ufw_allow() {
  local rule="$1"
  if have ufw; then
    ufw allow "$rule" >/dev/null || true
  fi
}

ufw_delete_allow() {
  local rule="$1"
  if have ufw; then
    ufw delete allow "$rule" >/dev/null 2>&1 || true
  fi
}

print_firewall() {
  if have ufw; then
    ufw status verbose || true
  else
    printf 'ufw=missing\n'
  fi
}

prepare_firewall() {
  local new_port="$1"
  local old_ports="$2"
  printf 'firewall_before:\n'
  print_firewall
  for port in $old_ports; do
    ufw_allow "$port/tcp"
  done
  ufw_allow "$new_port/tcp"
  ufw_allow "443/tcp"
  ufw_allow "443/udp"
  for port in $FALLBACK_PORTS; do
    ufw_allow "$port/tcp"
    ufw_allow "$port/udp"
  done
  printf 'firewall_after:\n'
  print_firewall
}

ensure_user_access() {
  local home
  if ! id "$TARGET_USER" >/dev/null 2>&1; then
    adduser --disabled-password --gecos "" "$TARGET_USER"
  fi

  if getent group sudo >/dev/null 2>&1; then
    usermod -aG sudo "$TARGET_USER"
  elif getent group wheel >/dev/null 2>&1; then
    usermod -aG wheel "$TARGET_USER"
  else
    printf 'WARNING: sudo/wheel group not found. Install/configure sudo before disabling root access.\n'
  fi

  home="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
  [ -n "$home" ] || die "Cannot determine home for $TARGET_USER"
  install -d -m 700 -o "$TARGET_USER" -g "$TARGET_USER" "$home/.ssh"
  if [ ! -f "$home/.ssh/authorized_keys" ]; then
    install -m 600 -o "$TARGET_USER" -g "$TARGET_USER" /dev/null "$home/.ssh/authorized_keys"
    printf 'WARNING: %s/.ssh/authorized_keys was missing and is now empty. Copy a public key before continuing.\n' "$home"
  fi
  chown "$TARGET_USER:$TARGET_USER" "$home/.ssh" "$home/.ssh/authorized_keys"
  chmod 700 "$home/.ssh"
  chmod 600 "$home/.ssh/authorized_keys"
}

write_hardening_config() {
  local mode="$1"
  local new_port="$2"
  local old_ports="$3"
  local dest
  local ports=""

  if [ "$mode" = "safe" ]; then
    for port in $old_ports; do
      ports="${ports}Port ${port}
"
    done
  fi
  ports="${ports}Port ${new_port}"

  local body
  if [ "$mode" = "safe" ]; then
    body="${ports}
PermitRootLogin prohibit-password
PasswordAuthentication no
PubkeyAuthentication yes
KbdInteractiveAuthentication no
ChallengeResponseAuthentication no
UsePAM yes
X11Forwarding no
MaxAuthTries 3
ClientAliveInterval 300
ClientAliveCountMax 2"
  else
    body="${ports}
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
KbdInteractiveAuthentication no
ChallengeResponseAuthentication no
UsePAM yes
X11Forwarding no
MaxAuthTries 3
ClientAliveInterval 300
ClientAliveCountMax 2"
  fi

  if supports_dropin; then
    dest="$HARDENING_DROPIN"
    printf '%s\n' "$body" >"$dest"
  else
    dest="/etc/ssh/sshd_config"
    awk -v begin="$MANAGED_BEGIN" -v end="$MANAGED_END" '
      $0 == begin { skip=1; next }
      $0 == end { skip=0; next }
      !skip { print }
    ' "$dest" >"${dest}.tmp"
    {
      cat "${dest}.tmp"
      printf '\n%s\n%s\n%s\n' "$MANAGED_BEGIN" "$body" "$MANAGED_END"
    } >"$dest"
    rm -f "${dest}.tmp"
  fi
  printf 'sshd_hardening_config=%s\n' "$dest"
}

rollback() {
  load_state
  [ -f "$BACKUP_FILE" ] || die "Backup not found: $BACKUP_FILE"
  cp "$BACKUP_FILE" /etc/ssh/sshd_config
  rm -f "$HARDENING_DROPIN"
  validate_sshd || die "Rollback config did not validate; inspect /etc/ssh/sshd_config manually before restart."
  restart_ssh "$SSH_SERVICE"
  printf 'rollback_done=True\n'
  printf 'restored_backup=%s\n' "$BACKUP_FILE"
}

stage0() {
  local script_dir
  script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  bash "$script_dir/ssh-hardening-preflight.sh"
}

stage1() {
  ensure_user_access
  printf 'stage=1\n'
  printf 'linuxuser_exists=True\n'
  printf 'root_access_changed=False\n'
  printf 'password_login_changed=False\n'
  printf 'ssh_port_changed=False\n'
}

stage2() {
  local ip ports
  ip="$(public_ip)"
  ports="$(current_ports)"
  printf 'stage=2\n'
  printf 'Test linuxuser key login from a NEW terminal before continuing:\n'
  for port in $ports; do
    if [ "$port" = "22" ]; then
      printf '  ssh %s@%s\n' "$TARGET_USER" "$ip"
    else
      printf '  ssh -p %s %s@%s\n' "$port" "$TARGET_USER" "$ip"
    fi
  done
  printf 'Do not continue to stage 4 until this login succeeds without a password.\n'
}

stage3() {
  local old_ports new_port backup
  old_ports="$(current_ports)"
  new_port="$(choose_port)"
  backup="/etc/ssh/sshd_config.backup.$(date +%Y%m%d-%H%M%S)"
  cp /etc/ssh/sshd_config "$backup"
  save_state "$new_port" "$old_ports" "$backup"
  prepare_firewall "$new_port" "$old_ports"
  printf 'stage=3\n'
  printf 'current_ssh_port=%s\n' "$old_ports"
  printf 'new_ssh_port=%s\n' "$new_port"
  printf 'backup_file=%s\n' "$backup"
  printf 'sshd_config_changed=False\n'
}

stage4() {
  load_state
  [ "${CONFIRM_LINUXUSER_KEY_LOGIN:-}" = "YES" ] || die "Refusing SSH policy changes. First verify linuxuser key login, then rerun with --confirm-linuxuser-key-login."
  write_hardening_config safe "$NEW_SSH_PORT" "$OLD_SSH_PORTS"
  if ! validate_sshd; then
    cp "$BACKUP_FILE" /etc/ssh/sshd_config
    rm -f "$HARDENING_DROPIN"
    die "sshd -t failed. Rolled back config and did not restart SSH."
  fi
  printf 'stage=4\n'
  printf 'safe_mode=True\n'
  printf 'root_login_policy=prohibit-password\n'
  printf 'password_login_disabled=True\n'
  printf 'old_ports_kept=True\n'
}

stage5() {
  load_state
  validate_sshd || die "sshd config invalid. Not restarting SSH."
  restart_ssh "$SSH_SERVICE"
  printf 'stage=5\n'
  printf 'ssh_restart=True\n'
  printf 'Do not close this session. Test from a NEW terminal:\n'
  printf '  ssh -p %s %s@%s\n' "$NEW_SSH_PORT" "$TARGET_USER" "$(public_ip)"
}

stage6() {
  load_state
  [ "${CONFIRM_NEW_PORT_LOGIN:-}" = "YES" ] || die "Refusing final lockdown. First verify: ssh -p $NEW_SSH_PORT $TARGET_USER@$(public_ip), then rerun with --confirm-new-port-login."
  write_hardening_config final "$NEW_SSH_PORT" "$OLD_SSH_PORTS"
  if ! validate_sshd; then
    cp "$BACKUP_FILE" /etc/ssh/sshd_config
    rm -f "$HARDENING_DROPIN"
    die "sshd -t failed. Rolled back config and did not restart SSH."
  fi
  for port in $OLD_SSH_PORTS; do
    if [ "$port" != "$NEW_SSH_PORT" ]; then
      ufw_delete_allow "$port/tcp"
    fi
  done
  restart_ssh "$SSH_SERVICE"
  printf 'stage=6\n'
  printf 'root_password_login_disabled=True\n'
  printf 'password_login_disabled=True\n'
  printf 'root_login_disabled=True\n'
  printf 'linuxuser_key_login_verified=True\n'
  printf 'new_ssh_port=%s\n' "$NEW_SSH_PORT"
  printf 'Verify root is denied from another terminal:\n'
  printf '  ssh -p %s root@%s\n' "$NEW_SSH_PORT" "$(public_ip)"
}

stage=""
rollback_requested="False"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --stage)
      shift
      stage="${1:-}"
      ;;
    --confirm-linuxuser-key-login)
      export CONFIRM_LINUXUSER_KEY_LOGIN=YES
      ;;
    --confirm-new-port-login)
      export CONFIRM_NEW_PORT_LOGIN=YES
      ;;
    --rollback)
      rollback_requested="True"
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die "Unknown argument: $1"
      ;;
  esac
  shift
done

require_root

if [ "$rollback_requested" = "True" ]; then
  rollback
  exit 0
fi

case "$stage" in
  0) stage0 ;;
  1) stage1 ;;
  2) stage2 ;;
  3) stage3 ;;
  4) stage4 ;;
  5) stage5 ;;
  6) stage6 ;;
  *) usage; exit 2 ;;
esac
