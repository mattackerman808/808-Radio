#!/bin/sh
# Sets up a Raspberry Pi (or any Debian/Ubuntu machine) as a network dongle for 808 Radio:
# installs rtl_tcp, keeps the kernel's DVB-T driver off the dongle, and runs rtl_tcp as a service on port 1234.
#
#   curl -fsSL https://raw.githubusercontent.com/mattackerman808/808-Radio/main/pi/install-rtl-tcp.sh | sudo sh
#
# rtl_tcp has no password: only run it on a network you trust, never port-forwarded to the internet.
set -eu

if [ "$(id -u)" != 0 ]; then echo "Run as root (sudo sh $0)"; exit 1; fi

echo "== Installing rtl-sdr"
DEBIAN_FRONTEND=noninteractive apt-get install -y rtl-sdr

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
if [ -f "$(dirname "$0")/rtl-tcp.service" ]; then
    cp "$(dirname "$0")/rtl-tcp.service" /etc/systemd/system/rtl-tcp.service
else
    curl -fsSL https://raw.githubusercontent.com/mattackerman808/808-Radio/main/pi/rtl-tcp.service \
        -o /etc/systemd/system/rtl-tcp.service
fi
systemctl daemon-reload
systemctl enable rtl-tcp.service
systemctl restart rtl-tcp.service
sleep 2

if systemctl is-active --quiet rtl-tcp.service; then
    echo
    echo "rtl_tcp is running. In 808 Radio: right-click > Source > Network dongle (rtl_tcp), and enter:"
    for a in $(hostname -I); do case "$a" in *:*) ;; *) echo "    $a" ;; esac; done
    echo "    or $(hostname).local"
else
    echo
    echo "rtl_tcp isn't running yet (is the dongle plugged in?). It retries every few seconds. Log:"
    journalctl -u rtl-tcp.service -n 10 --no-pager
fi
