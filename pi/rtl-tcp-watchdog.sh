#!/bin/sh
# Watchdog for rtl_tcp (installed by install-rtl-tcp.sh, run by rtl-tcp-watchdog.service as root).
#
# rtl_tcp occasionally wedges when a client disconnects: it leaves that connection half-closed (CLOSE-WAIT), spins a
# CPU, and stops serving; sometimes the dongle is left stuck too, so even a fresh rtl_tcp accepts clients but streams
# nothing. Every 5 s this checks port 1234, and if a connection has been half-closed, or a connected client hasn't been
# sent anything new, for 10 s, it stops rtl_tcp, resets the dongle's USB connection, and starts rtl_tcp again.
PORT=1234
closewait=0; stalled=0; last=""

reset_dongle() {
    for d in /sys/bus/usb/devices/*; do
        [ -f "$d/idVendor" ] || continue
        case "$(cat "$d/idVendor"):$(cat "$d/idProduct")" in
            0bda:2832|0bda:2838)
                echo 0 > "$d/authorized"; sleep 1; echo 1 > "$d/authorized"
                echo "reset USB device $d" ;;
        esac
    done
}

while true; do
    sleep 5
    if ss -tnH state close-wait sport = :$PORT | grep -q .; then closewait=$((closewait + 1)); else closewait=0; fi
    # total sent to all connected clients (a second client waiting in the queue has nothing sent, which is normal)
    sent=$(ss -tnHi state established sport = :$PORT | grep -o 'bytes_sent:[0-9]*' | cut -d: -f2 | awk '{s += $1} END {if (NR) print s}')
    if [ -n "$sent" ] && [ "$sent" = "$last" ]; then stalled=$((stalled + 1)); else stalled=0; fi
    last="$sent"
    if [ "$closewait" -ge 2 ] || [ "$stalled" -ge 2 ]; then
        echo "rtl_tcp stuck (half-closed checks: $closewait, no-data checks: $stalled): restarting it and the dongle"
        systemctl stop rtl-tcp.service
        reset_dongle
        sleep 2
        systemctl start rtl-tcp.service
        closewait=0; stalled=0; last=""
        sleep 10
    fi
done
