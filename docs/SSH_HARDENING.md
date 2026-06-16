# Safe SSH Hardening For Shanlian VPS

These scripts harden SSH in stages so the current root session is not locked out.
Do not close the current SSH session until the final verification is complete.

## Files

- `tools/ssh-hardening-preflight.sh`: read-only preflight report.
- `tools/ssh-hardening-staged.sh`: staged hardening workflow with explicit confirmations.

## Stage 0: Preflight Only

Run on the VPS:

```bash
sudo bash tools/ssh-hardening-preflight.sh
```

This prints:

- current user and SSH service name
- current SSH port
- server public IP
- `linuxuser` existence, sudo group, and `authorized_keys` permissions
- firewall state
- listening ports
- Hysteria2 or sing-box service hints

It does not change anything.

## Stage 1: Prepare Safe Access

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 1
```

This only prepares `linuxuser`:

- creates `linuxuser` if missing
- adds it to `sudo` or `wheel`
- fixes `/home/linuxuser/.ssh` to `700`
- fixes `/home/linuxuser/.ssh/authorized_keys` to `600`
- fixes ownership to `linuxuser:linuxuser`

It does not disable root, password login, or change SSH ports.

If `authorized_keys` is empty, add your public key before continuing.

## Stage 2: Test Existing linuxuser Login

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 2
```

The script prints the exact command to run from a new terminal, for example:

```bash
ssh linuxuser@SERVER_IP
```

If the current SSH port is not 22, it prints:

```bash
ssh -p CURRENT_PORT linuxuser@SERVER_IP
```

Continue only after `linuxuser` key login succeeds from a separate terminal.

## Stage 3: Prepare New Port And Firewall

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 3
```

This chooses `28422` unless it is already occupied, then opens:

- current SSH port
- new SSH port
- `443/tcp`
- `443/udp`
- fallback ports `8443`, `2053`, `2087`, `2096`, `2083` on both TCP and UDP

If `ufw` is inactive, the rules are added but `ufw` is not enabled by the script.

The script also creates:

```text
/etc/ssh/sshd_config.backup.YYYYMMDD-HHMMSS
/root/ssh-hardening-state.env
```

## Stage 4: Apply Safe SSH Config

Only run this after Stage 2 has been verified from a new terminal:

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 4 --confirm-linuxuser-key-login
```

This keeps the old SSH port and new SSH port active, and sets:

```text
PermitRootLogin prohibit-password
PasswordAuthentication no
PubkeyAuthentication yes
KbdInteractiveAuthentication no
ChallengeResponseAuthentication no
UsePAM yes
X11Forwarding no
MaxAuthTries 3
ClientAliveInterval 300
ClientAliveCountMax 2
```

It validates with:

```bash
sshd -t
```

If validation fails, it rolls back and does not restart SSH.

## Stage 5: Restart SSH Safely

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 5
```

Do not close the current session.

The script prints the command to test from a new terminal:

```bash
ssh -p NEW_PORT linuxuser@SERVER_IP
```

Continue only after this succeeds.

## Stage 6: Final Lockdown

Only run this after new-port `linuxuser` key login succeeds from a new terminal:

```bash
sudo bash tools/ssh-hardening-staged.sh --stage 6 --confirm-new-port-login
```

This changes:

```text
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
```

It removes old SSH port firewall rules after the new port is confirmed, validates `sshd -t`, and restarts SSH.

Then verify from a new terminal:

```bash
ssh -p NEW_PORT linuxuser@SERVER_IP
ssh -p NEW_PORT root@SERVER_IP
```

Expected result:

- `linuxuser` key login works.
- root login is denied.
- password login is denied.
- Hysteria2/VPN ports are still allowed.

## Rollback

If anything looks wrong while the current session is still open:

```bash
sudo bash tools/ssh-hardening-staged.sh --rollback
```

Manual rollback:

```bash
sudo cp /etc/ssh/sshd_config.backup.YYYYMMDD-HHMMSS /etc/ssh/sshd_config
sudo rm -f /etc/ssh/sshd_config.d/99-hardening.conf
sudo sshd -t
sudo systemctl restart ssh || sudo systemctl restart sshd
```

If `ufw` rules need to be restored manually:

```bash
sudo ufw allow CURRENT_PORT/tcp
sudo ufw allow 443/tcp
sudo ufw allow 443/udp
sudo ufw allow 8443/tcp
sudo ufw allow 8443/udp
sudo ufw allow 2053/tcp
sudo ufw allow 2053/udp
sudo ufw allow 2087/tcp
sudo ufw allow 2087/udp
sudo ufw allow 2096/tcp
sudo ufw allow 2096/udp
sudo ufw allow 2083/tcp
sudo ufw allow 2083/udp
sudo ufw status verbose
```

## Final Report Format

```text
linuxuser_exists=True/False
authorized_keys_ok=True/False
linuxuser_sudo=True/False
current_ssh_port=
new_ssh_port=
sshd_config_test=True/False
firewall_ok=True/False
root_password_login_disabled=True/False
password_login_disabled=True/False
linuxuser_key_login_verified=True/False
hysteria_ports_preserved=True/False
lockout_risk=low/medium/high
```
