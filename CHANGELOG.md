# Changelog

## Unreleased

- Instrument panel: the network dongle's host and data rate share one **Source** line, so the statistics no
  longer run into the equalizer section; shorter Tuning and Gain lines (they were cut off).
- **Instrument panel on the GPU.** The spectrum, waterfall, multiplex and meters are drawn with Direct2D in their own
  window, on a render thread, presented in step with the display (flip-model swap chain): every refresh by default
  (right-click → Panel frame rate: display / 120 / 60 / 30), about 1.3 ms of CPU a frame, and the UI thread stays
  free (it was 40–70% busy drawing). The waterfall glides between rows, and the traces respond faster. GDI+ still
  draws the rest, the flip animation, and everything if Direct3D isn't available (then at up to 60 fps, paced off
  the display). Also fixed for all painting: WinForms allocated a new full-window back buffer on every paint (its
  default MaximumBuffer is 225 x 96), ~4 ms a frame at 4K.
- `808Radio.exe --bench <recordings> <seconds> <report> [width] [fps]`: paint timings with the panel open.

## 0.2.0 (2026-10-01)

- **Network dongle**: use an RTL-SDR on another machine (e.g. a Raspberry Pi by the antenna) through the standard
  `rtl_tcp` server: right-click → Source → Network dongle. Gain, frequency correction and antenna power work over the
  network; the stream is drained continuously so the radio stays live, retunes and the automatic gain allow for the
  link's latency, and the connection is re-established by itself after a reboot or network drop.
- **Automatic discovery**: Pis set up with the script advertise themselves (mDNS / DNS-SD, `_rtl-tcp._tcp`) and are
  listed under Source; with no USB dongle, 808 Radio switches to one it finds.
- `pi/install-rtl-tcp.sh`: one-line setup of rtl_tcp as an advertised service on a Pi; guide in `docs/raspberry-pi.md`.
- Instrument panel: **Link** line (data rate, queue, drops) for a network dongle.
- Tools: `discover`, `netlatency`; `play` can use rtl_tcp (`R808_RTLTCP=host`).

## 0.1.0 (2026-09-30)

First release.

- **FM receiver** for RTL-SDR dongles (osmocom librtlsdr): stereo with pilot-SNR-based blend, 75 µs de-emphasis,
  multipath equalizer (blind constant-modulus), RDS/RBDS (call sign from PI, PS, RadioText, PTY).
- **HD Radio** via nrsc5: HD1–HD8, station name and slogan, song and artist, album art and station logos, weather and
  traffic maps, emergency alerts. HD audio is time-aligned to the analog and blended like an HD receiver
  (crossfades ahead of dropouts, weak-signal hysteresis, loudness matching).
- **Automatic gain**: peaks the tuner gain for each station and keeps it there, guarding against ADC overload.
- **Automatic frequency correction**, measured from FM carriers; bias-tee option.
- **Car-stereo UI**: frameless single-DIN faceplate with a dot-matrix display, six illumination colors, volume knob,
  seek, six presets, signal meter and overload indicator.
- **Flip-down instrument panel**: spectrum and waterfall with click-to-tune, FM multiplex spectrum, RF/FM/RDS/HD/audio
  statistics, equalizer taps, history graphs, audio meters.
- Self-contained single-file build for Windows x64; settings in `%APPDATA%\808Radio`.
- Command-line tools (`radio808-tools`): capture, spectrum, scan, offline FM/HD decoding with stats, self-test,
  gain sweep, control stress test, replay.
