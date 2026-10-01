# Network dongle: the RTL-SDR on a Raspberry Pi

Put the dongle where the antenna is (attic, roof, window) on a Raspberry Pi, and run 808 Radio on any PC on your
network. The Pi runs the standard `rtl_tcp` server and streams the dongle's raw I/Q samples; everything else (FM, HD,
RDS, automatic gain and frequency correction, the instrument panel) works as with a USB dongle.

## What you need

- A Raspberry Pi 3, 4 or 5 (or any Linux machine) running Raspberry Pi OS / Debian / Ubuntu.
- **A wired network connection is best.** The stream is about **24 Mbit/s** (1.49 million 8-bit I/Q samples a
  second), all the time. Gigabit or 100 Mbit Ethernet is plenty; 5 GHz Wi-Fi usually works; 2.4 GHz Wi-Fi (e.g. a Pi
  Zero 2 W) generally can't keep up.
- The dongle (RTL-SDR Blog V3 tested). For an RTL-SDR Blog **V4**, install the RTL-SDR Blog fork of rtl-sdr instead
  of the distribution package; it has its own `rtl_tcp` and the rest of this page applies.

## Setup

On the Pi, with the dongle plugged in:

```
curl -fsSL https://raw.githubusercontent.com/mattackerman808/808-Radio/main/pi/install-rtl-tcp.sh | sudo sh
```

(or copy the files in [`pi/`](../pi) over and run `sudo sh install-rtl-tcp.sh`). It:

1. installs the `rtl-sdr` and `avahi-daemon` packages,
2. stops the kernel's DVB-T TV driver from claiming the dongle,
3. installs and starts `rtl-tcp.service`: `rtl_tcp` on port 1234, started at boot and restarted if it stops or the
   dongle is replugged,
4. advertises it on the network (mDNS / DNS-SD, service type `_rtl-tcp._tcp`) so 808 Radio can find it.

Then in 808 Radio, right-click → **Source**: the Pi is listed as **rtl_tcp on _hostname_**; click it. If there's no
dongle plugged into the PC, 808 Radio looks on the network by itself and switches to the first one it finds. It
remembers the choice and reconnects by itself if the Pi reboots or the network drops. **Source → USB dongle** switches
back.

For a server that isn't advertised (another network, a different setup), choose **Network dongle (rtl_tcp)…** and
enter its name or address (e.g. `raspberrypi.local` or `192.168.1.50`; add `:port` if it isn't 1234).

The instrument panel's **Source** line shows the Pi and the data rate (about 24 Mb/s), plus any dropped blocks.

## Notes

- **One listener at a time:** `rtl_tcp` serves one client. A second 808 Radio (or SDR#) connecting takes over when the
  first disconnects.
- **No password:** anyone who can reach port 1234 can use the dongle. Keep it on your home network; don't forward the
  port to the internet. (To listen from outside, use a VPN such as WireGuard or Tailscale into your network.)
- **Antenna power** (bias-tee) from the right-click menu works over the network too; same caution as locally.
- **Other software:** SDR#, SDR++, GQRX and others can use the same server (`rtl_tcp` source).
- Logs on the Pi: `journalctl -u rtl-tcp -f`. Restart: `sudo systemctl restart rtl-tcp`.
- 808 Radio drains the stream continuously and drops audio (rather than falling behind) if the PC can't keep up, so
  the radio stays live; the service also caps `rtl_tcp`'s own backlog at about 2 seconds.
