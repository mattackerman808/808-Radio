# Changelog

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
