# 🚽 Toilet Terminal

```
 ██████╗██╗      █████╗ ██╗   ██╗██████╗ ███████╗
██╔════╝██║     ██╔══██╗██║   ██║██╔══██╗██╔════╝
██║     ██║     ███████║██║   ██║██║  ██║█████╗
██║     ██║     ██╔══██║██║   ██║██║  ██║██╔══╝
╚██████╗███████╗██║  ██║╚██████╔╝██████╔╝███████╗
 ╚═════╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚═════╝ ╚══════╝

████████╗ ██████╗ ██╗██╗     ███████╗████████╗
╚══██╔══╝██╔═══██╗██║██║     ██╔════╝╚══██╔══╝
   ██║   ██║   ██║██║██║     █████╗     ██║
   ██║   ██║   ██║██║██║     ██╔══╝     ██║
   ██║   ╚██████╔╝██║███████╗███████╗   ██║
   ╚═╝    ╚═════╝ ╚═╝╚══════╝╚══════╝   ╚═╝

 ██████╗██╗     ██╗███████╗███╗   ██╗████████╗
██╔════╝██║     ██║██╔════╝████╗  ██║╚══██╔══╝
██║     ██║     ██║█████╗  ██╔██╗ ██║   ██║
██║     ██║     ██║██╔══╝  ██║╚██╗██║   ██║
╚██████╗███████╗██║███████╗██║ ╚████║   ██║
 ╚═════╝╚══════╝╚═╝╚══════╝╚═╝  ╚═══╝   ╚═╝
```

A web-based terminal client that lets you access your Linux PC's terminal sessions from a phone browser on your local network.

Built for those critical moments when nature calls but Claude Code is asking "Do you want to proceed with the changes?" and you're already mid-sprint... to the bathroom.

**The problem:** You're pair-programming with Claude, it asks for approval, but you really need to go. Do you hold it? Do you let Claude wait? Neither. You pull out your phone, open Toilet Terminal, and hit that Shift+Tab like a civilized engineer.

**No more choosing between productivity and biology.**

## Features

- 🖥️ **Full terminal in the browser** — xterm.js with 256-color support, resize handling, mobile keyboard support
- 🔍 **Session discovery** — automatically finds your active terminal sessions (PTY) and tmux sessions on the host. Find your Claude Code session in seconds
- ⌨️ **Shortcut buttons** — Shift+Tab (Claude auto-mode accept), Ctrl+C, Esc — right there in the top bar, thumb-friendly
- 🔒 **Password authentication** — so your roommate can't `rm -rf /` while you're in the shower
- 🌐 **Subnet restriction** — limit access to your LAN and/or VPN. Paranoia-friendly
- 🐳 **Docker-packaged** — single `docker compose up` and you're in business (or in the bathroom, same thing)

## Quick Start

### Prerequisites

- Linux host (bare metal or WSL2)
- Docker and Docker Compose
- A toilet (optional but recommended)

### 1. Clone and configure

```bash
git clone https://github.com/yourusername/claude-toilet-client.git
cd claude-toilet-client
```

Edit `docker-compose.yml` and set your password:

```yaml
environment:
  - AUTH_PASSWORD=your-secret-password
```

### 2. Launch the throne

```bash
docker compose up -d
```

### 3. Open on your phone

Navigate to `http://<your-pc-ip>:5000` in your phone browser. Enter the password and you're in.

To find your PC's local IP:

```bash
hostname -I | awk '{print $1}'
```

Pro tip: bookmark it. You'll be using this more than you'd like to admit.

## Configuration

All configuration is done via environment variables in `docker-compose.yml`:

| Variable | Default | Description |
|---|---|---|
| `AUTH_PASSWORD` | `toilet123` | Password for the login page. **Please change this.** Unless you find it funny. |
| `ALLOWED_SUBNETS` | `192.168.0.0/16,10.0.0.0/8` | Comma-separated CIDR ranges allowed to connect. Leave empty to disable (password-only). |
| `HOST_USER` | `$USER` | The Linux user on the host whose sessions to access. |

### Example: LAN + WireGuard VPN

```yaml
environment:
  - AUTH_PASSWORD=my-strong-password
  - ALLOWED_SUBNETS=192.168.1.0/24,10.8.0.0/24
```

### Example: Password-only (no subnet restriction)

```yaml
environment:
  - AUTH_PASSWORD=my-strong-password
  - ALLOWED_SUBNETS=
```

## How It Works

```
📱 Phone (on the toilet)
    │
    │  Blazor Server (SignalR WebSocket)
    │
    ▼
🐳 Docker Container
    │
    │  nsenter → host namespaces
    │
    ▼
🖥️ Host Linux (forkpty → bash/tmux)
```

The app runs in Docker with `network_mode: host`, `pid: host`, and `privileged: true`. It uses `nsenter` to break into the host's namespaces and spawn terminal sessions as your host user via `forkpty()`. A native C helper library handles the PTY fork to avoid corrupting the .NET runtime.

Session discovery scans `/proc` on the host to find active PTY sessions and running tmux sessions, so you can pick which terminal to connect to from the dropdown.

## Docker Compose Reference

```yaml
services:
  toilet-client:
    build: .
    container_name: claude-toilet-client
    network_mode: host        # accessible at host IP:5000
    pid: host                 # can discover host terminal sessions
    privileged: true          # required for nsenter into host
    environment:
      - AUTH_PASSWORD=toilet123
      - ALLOWED_SUBNETS=192.168.0.0/16,10.0.0.0/8
      - HOST_USER=${USER:-root}
    restart: unless-stopped
```

## Rebuilding

After making changes:

```bash
docker compose up -d --build
```

## Tech Stack

- .NET 8 Blazor Server
- XtermBlazor (xterm.js wrapper)
- Native C library for PTY management (forkpty/execvp)
- Docker with host networking

---

*Made with ❤️ and urgency*
