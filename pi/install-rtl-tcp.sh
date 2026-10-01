#!/bin/sh
# Sets up a Raspberry Pi (or any Debian/Ubuntu machine) as a network dongle for 808 Radio:
# installs rtl_tcp, keeps the kernel's DVB-T driver off the dongle, and runs rtl_tcp as a service on port 1234.
#
#   curl -fsSL https://raw.githubusercontent.com/mattackerman808/808-Radio/main/pi/install-rtl-tcp.sh | sudo sh
#
# rtl_tcp has no password: only run it on a network you trust, never port-forwarded to the internet.
set -eu

if [ "$(id -u)" != 0 ]; then echo "Run as root (sudo sh $0)"; exit 1; fi

# install <file in pi/> <destination>: from next to this script, or from GitHub when piped through curl
fetch() {
    if [ -f "$(dirname "$0")/$1" ]; then cp "$(dirname "$0")/$1" "$2"
    else curl -fsSL "https://raw.githubusercontent.com/mattackerman808/808-Radio/main/pi/$1" -o "$2"; fi
}

echo "== Installing rtl-sdr and avahi (so 808 Radio finds this machine by itself)"
DEBIAN_FRONTEND=noninteractive apt-get install -y rtl-sdr avahi-daemon

echo "== Keeping the DVB-T TV driver off the dongle"
cat > /etc/modprobe.d/rtl-sdr-blacklist.conf <<'EOF'
# the RTL2832U is a TV tuner chip: stop the kernel's DVB-T driver claiming it, so rtl_tcp can
blacklist dvb_usb_rtl28xxu
blacklist rtl2832
blacklist rtl2832_sdr
blacklist r820t
EOF
for m in rtl2832_sdr dvb_usb_rtl28xxu rtl2832 r820t; do modprobe -r "$m" 2>/dev/null || true; done

echo "== Installing the rtl-tcp service"
fetch rtl-tcp.service /etc/systemd/system/rtl-tcp.service
mkdir -p /etc/avahi/services
fetch rtl-tcp.avahi.service /etc/avahi/services/rtl-tcp.service   # avahi picks it up by itself
fetch rtl-tcp-watchdog.sh /usr/local/bin/rtl-tcp-watchdog.sh   # restarts rtl_tcp if it wedges
fetch rtl-tcp-watchdog.service /etc/systemd/system/rtl-tcp-watchdog.service
systemctl daemon-reload
systemctl enable rtl-tcp.service
systemctl restart rtl-tcp.service
systemctl enable rtl-tcp-watchdog.service
systemctl restart rtl-tcp-watchdog.service
sleep 2

if systemctl is-active --quiet rtl-tcp.service; then
    echo
    echo "rtl_tcp is running. 808 Radio finds it by itself: right-click > Source > \"rtl_tcp on $(hostname)\"."
    echo "(If it isn't listed, choose Network dongle (rtl_tcp)... and enter $(hostname).local or one of:"
    for a in $(hostname -I); do case "$a" in *:*) ;; *) echo "    $a" ;; esac; done
    echo ")"
else
    echo
    echo "rtl_tcp isn't running yet (is the dongle plugged in?). It retries every few seconds. Log:"
    journalctl -u rtl-tcp.service -n 10 --no-pager
fi
