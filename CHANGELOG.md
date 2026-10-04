# Changelog

## Unreleased

- **The TUNE knob works with the wheel again.** Since the knobs moved to the right-hand corner (0.7.0), scrolling over
  the TUNE knob changed the volume: the wheel treated the whole right edge as the volume knob. The wheel now changes the
  volume only over the VOL knob; over the TUNE knob (and the display) it tunes. Both platforms.

## 0.7.0 (2026-10-03)

- **The weather map on Windows.** The HD weather radar is drawn over a street map of the same box on both
  platforms now; the map code (Swiftcamp's basemap archive, the vector tile decoder, the style) moved to the
  shared project, with Avalonia and GDI+ each drawing it.
- **The map works offline.** As soon as a station's weather box is known, every map tile it could need is
  fetched and kept (a few dozen), so the radar has its street map without the internet. The archive is checked
  for updates once a day; a failed fetch is retried every five minutes.
- **The station tells you what it sends.** The HD decoder reads the Service Information Guide, so WX and TRAFFIC
  show as soon as a station is tuned: dark where there are no maps, blinking in the display once the station
  promises them, lit when the first one arrives (the panel lists the station's data services).
- **HD-only seek.** The HD SEEK key (also in the menu): seek skips analog-only stations, waiting a few seconds on
  each for HD sync. The display's [HD][SEEK] lights show it at work.
- **A new faceplate layout.** Nine same-size keys left of the display in three rows: OPEN, DISP, COLOR / MUTE, WX,
  TRAFFIC / SRC, HD CH, HD SEEK. The tune knob sits above a tune-sized volume knob in the corner. The signal
  meter moved into the display as an antenna with five dots and an OVL light. The brand reads "808 Radio" at the
  left and a DIGITAL badge sits at the right.
- Development: `--mapsnap` fills the map cache like the app does.

## 0.6.1 (2026-10-03)

- **Resizing works on the Mac.** Dragging the faceplate's edges did nothing: Avalonia's macOS backend has no
  system resize drag for a frameless window. The window now follows the pointer itself, keeping the faceplate's
  shape; a left or top edge keeps the opposite edge in place.
- **Crisp dots on non-Retina monitors.** The HD programs strip smeared on a 1x display, where its dots were under
  two pixels across. Dots under three pixels of pitch are drawn pixel-aligned on a whole-pixel pitch (Mac app).
- **No analyzer ghost over the panel.** The audio analyzer square stayed on screen when the faceplate folded down
  (and over album art or MUTE); it's hidden when it isn't live. Closing the panel no longer leaves the window tall.
- Development: `R808_SNAP_SCALE=1` renders `--snapshot` at 1x, as a non-Retina monitor shows it.

## 0.6.0 (2026-10-03)

- **808 Radio for macOS** (Apple Silicon): the faceplate, the flip-down instrument panel, the right-click menu and
  the shortcuts, a USB dongle or the network dongle on a Raspberry Pi, with nothing to install: the download is a
  signed and notarized app with the native libraries inside. The receiver and HD decoding are the same code as
  Windows; the user interface is a port onto Avalonia. See `docs/mac-port.md`.
- **Weather radar on a map** (macOS): the HD weather image is a radar layer whose map corners the broadcast gives,
  so the Mac app draws it over a street map of the same box (OpenStreetMap data from the Swiftcamp basemap archive,
  cached), with the time it was received. The traffic mosaic fills in live as its tiles arrive.
- **Traffic tiles in the right places.** Tiles come as parts 1-9 in row-major order from the north-west corner
  with 0-based row and column in their names; the 1-based parser put four of them in the wrong cell. Both platforms.
- **The console tool** prints per-sideband HD MER, the bit error rate, the files and map images a station sends,
  and saves them with `R808_SAVE_DIR`; it builds and runs on macOS (a USB dongle there needs no driver).
- The settings and log live in `~/Library/Application Support/808Radio` on macOS; the controller, settings and log
  moved to a shared project used by both apps. Start failures are logged with their reason.
- **Releases are built by GitHub Actions** on a version tag: the Windows zip and the signed, notarized Mac zip.

## 0.5.0 (2026-10-01)

- **A tuning knob** on the right of the faceplate, under the signal meter, mirroring the volume knob like the classic
  two-knob head units: turn it (drag around it) to step a channel per notch, the frequency flashing on the display;
  the wheel over it tunes too, and a click seeks.

## 0.4.0 (2026-10-01)

- **Display reworked like a real head unit's:** a large dot-matrix line and a full-width small one with the clock;
  **DISP** steps through what they show (song / station, station / song, station / genre, artist / title,
  frequency / station; long text scrolls on both). Below them, a row of fixed status lights, each lit or dark, nothing
  moving: **HD**, **WEAK** (red), the classic interlocking-rings stereo indicator, **RDS**, **SEEK**, **WX**,
  **TRAFFIC**; then a mini dot matrix with the station's HD programs (`HD 1 2 3`: the one you're hearing lit, the one
  you've chosen blinking while it locks in, click one to switch). The HD1/HD2/… tags that came and went are gone, and
  the **BAND** key is renamed **HD CH**: it steps through the programs (`HD2/3 CLASSIC ROCK`). DGTL is folded into HD
  (filled: digital audio), the "HD IN 12S" countdown into WEAK, and the P1–P6 badge into the preset keys (the one
  you're on is outlined).
- **One signal meter**, the slot on the right of the faceplate, showing the reception quality of what you're
  hearing: HD MER while HD plays, else the FM pilot SNR (it used to switch to HD MER whenever HD was found, even when
  too weak to play), with an antenna icon under it. The small signal bars on the display, which duplicated it, are
  gone. DISP and HD CH swapped places (DISP on the left).
- The audio analyzer brightens toward the top for depth (one color, the illumination), with brighter peak ticks.
- Seven-segment readouts: the clock at the end of the small line, like a VFD clock, and each preset key's frequency
  in a little window (lit on the preset you're on, dimmer on the others, ghost 88.8 on an empty one).
- **MUTE** blinks in big red letters in the art square while muted (click it to unmute); the speaker key's little
  cross was easy to miss.
- The HD mini matrix pages like a head unit: the programs on air (`HD 1 2 3`), the format of the one you're hearing
  (`HD1 ADULT HITS`, scrolling if long), the programs again, the next program's format (dim), and round; click it
  for the next program.
- **WEAK** lights next to **HD** (and the instrument panel shows Sync "weak", Blend "HD too weak to play") when a station's HD
  is found, with its name and program list, but its bit error rate is too high for the audio to decode, so you're
  hearing the analog. Before, the HD badge and program tags suggested HD was playing.
- **Automatic frequency correction no longer wanders.** It applied single readings, which through rtl_tcp vary by
  about +-4 ppm from one to the next; over many restarts that walked the setting to +22 ppm (2 kHz off-tune, hurting
  HD). It now takes 9 readings over ~30 s, applies their median only if they agree (and at most 10 ppm at a time),
  and logs each decision to 808Radio.log. (A setting that already wandered: right-click, Frequency correction, Reset.)
- WEAK judges the median bit error rate of the last few HD frames, from 8 s after sync, ignoring the first frame:
  rough frames right after a sync no longer light it.
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
