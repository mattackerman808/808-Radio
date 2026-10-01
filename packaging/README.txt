808 Radio - FM + HD Radio for RTL-SDR dongles
https://github.com/mattackerman808/808-Radio

QUICK START
1. Plug in an RTL-SDR dongle (RTL2832U-based: RTL-SDR Blog V3/V4, NooElec, generic R820T sticks).
2. If no SDR program has used the dongle on this PC yet, install its WinUSB driver once with Zadig
   (https://zadig.akeo.ie): Options > List All Devices, pick "Bulk-In, Interface (Interface 0)",
   choose WinUSB, click Replace Driver. (If SDR# already works with the dongle, skip this.)
3. Run 808Radio.exe. No installer and no .NET install are needed. Keep the DLLs next to the exe.

Only one program can use the dongle at a time: close SDR# or other SDR software first.

NETWORK DONGLE
The dongle can also be on a Raspberry Pi (or other Linux machine) near the antenna, running rtl_tcp.
Setup on the Pi: https://github.com/mattackerman808/808-Radio/blob/main/docs/raspberry-pi.md
808 Radio finds the Pi by itself: right-click > Source lists it (or choose Network dongle and enter its address).
It needs about 24 Mbit/s: wired Ethernet, or good 5 GHz Wi-Fi.

USING IT
- Drag the faceplate to move it; drag its edges to resize.
- Volume: drag or scroll the knob (click it to mute). Tune: scroll over the display, or the arrow keys.
- Seek: the |<< >>| keys (or Ctrl+arrows). Presets 1-6: click to tune, hold to save.
- SRC switches HD / analog FM. BAND cycles HD programs (HD1, HD2 ...). DISP changes the display.
- The small key above the left end of the display flips the faceplate down to a panel with a spectrum and
  waterfall (click a station to tune), signal statistics and meters.
- The square on the right of the display shows HD album art (click it for a bigger picture) or an audio
  spectrum analyzer.
- Right-click for settings: source (USB or network dongle), colors, gain, frequency correction, antenna power
  (bias-tee), album art on/off, instrument panel frame rate, always on top.

Gain is automatic: 808 Radio finds the best gain for each station and avoids overloading the dongle.
Frequency correction is measured automatically from FM stations.

Settings are saved in %APPDATA%\808Radio\settings.json; errors are logged to 808Radio.log in the same folder.

808 Radio is free software under the GNU GPL v3 or later (LICENSE.txt). It includes nrsc5, FAAD2, FFTW,
librtlsdr, libusb, NAudio, Vortice.Windows and .NET; see THIRD_PARTY_NOTICES.md. Not affiliated with Xperi (HD Radio).
