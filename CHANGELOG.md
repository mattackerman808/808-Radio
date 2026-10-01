# Changelog

## Unreleased

- **Display reworked like a real head unit's:** the bottom row is fixed legends, lit or dark: **WX**, **TRF**,
  **SEEK** and **WEAK**. The HD1/HD2/… tags that came and went are gone; the HD program you hear is on the dot-matrix
  line (`HD2 107.7`), and **BAND** steps through the programs (`HD2/3 CLASSIC ROCK`). The "HD IN 12S" countdown text is
  folded into **WEAK**.
- **WEAK** lights (and the instrument panel shows Sync "weak", Blend "HD too weak to play") when a station's HD is
  found, with its name and program list, but its bit error rate is too high for the audio to decode, so you're
  hearing the analog. Before, the HD badge and program tags suggested HD was playing.
- **Network dongle recovers from a wedged rtl_tcp.** rtl_tcp can hang when a client disconnects (half-closed socket,
  a spinning CPU, sometimes a stuck dongle). The Pi setup now installs `rtl-tcp-watchdog.service`, which restarts
  rtl_tcp and resets the dongle's USB connection when that happens; 808 Radio also reconnects if the stream trickles
  in below 30% of its rate. Tested by freezing rtl_tcp: listening again within ~20 s, untouched.

## 0.3.0 (2026-10-01)

- Instrument panel: the network dongle's host and data rate share one **Source** line, so the statistics no
  longer run into the equalizer section; shorter Tuning and Gain lines (they were cut off).
- **Instrument panel on the GPU.** The spectrum, waterfall, multiplex and meters are drawn with Direct2D in their own
  window, on a render thread, presented in step with the display (flip-model swap chain): every refresh by default
  (right-click → Panel frame rate: display / 120 / 60 / 30), about 1.3 ms of CPU a frame, and the UI thread stays
  free (it was 40–70% busy drawing). The waterfall glides between rows, and the traces respond faster. GDI+ still
  draws the rest, the flip animation, and everything if Direct3D isn't available (then at up to 60 fps, paced off
  the display). Also fixed for all painting: WinForms allocated a new full-window back buffer on every paint (its
  default MaximumBuffer is 225 x 96), ~4 ms a frame at 4K.
- **Audio spectrum analyzer** on the faceplate, like an old head unit's: 16 bands from 40 Hz to 16 kHz of the audio
  that's playing (tilted +3 dB/octave so music reads level), bars that rise instantly and fall steadily, and peak
  ticks that hold and drop. It was a 16-band RF spectrum of the station's 744 kHz. 60 fps, repainting just its
  square (the rest of the faceplate stays at 10 Hz). Blank while muted.
- **Album art pops out:** click it on the display and it grows into a bigger picture with the song, artist and
  station; a click, Esc or clicking the radio closes it. Right-click → *Album art on the display* turns the art off
  so the analyzer is always there.
- `808Radio.exe --bench <recordings> <seconds> <report> [width] [fps]`: paint timings with the panel open.
  `R808_BENCH_CLOSED=1` times the faceplate instead; `R808_BENCH_ART=1` pops out the art. Tools: `analyzer <wav>`.

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
