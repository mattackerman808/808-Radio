# 808 Radio

A standalone FM receiver for Windows with HD Radio (NRSC-5) built in, for RTL-SDR dongles.
Car-radio style: tune, seek, presets, HD1–HD8, now-playing with album art.

Sister project of [808 HD](https://github.com/mattackerman808/808-HD), the HD Radio plugin for SDR#.

> Work in progress. Currently: FM stereo + HD Radio with automatic blending, playable from the command line
> (`radio808-tools play 97.3`). Next: RDS, then the car-radio UI.

## Hardware

- Any RTL2832U dongle with an R820T/R820T2/R828D tuner (RTL-SDR Blog V3/V4, NooElec, ...)
- The **WinUSB** driver, installed once with [Zadig](https://zadig.akeo.ie/) (if SDR# already works with the dongle, this is done)

## Building

Requirements (all free):

- .NET 9 SDK: `winget install Microsoft.DotNet.SDK.9`
- MSYS2 (builds the native libraries): `winget install MSYS2.MSYS2`

```
git clone --recursive https://github.com/mattackerman808/808-radio.git
cd 808-radio
```

Native libraries (first build takes several minutes), from an MSYS2 UCRT64 shell in the repo:

```
pacman -S --needed make mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-libusb mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-libtool autoconf automake patch git
./build-native.sh
```

Then:

```
dotnet build tools/Radio808.Tools -c Release
```

## Tools

`tools/Radio808.Tools` (`radio808-tools.exe`):

| Command | |
|---|---|
| `devices` | list RTL-SDR dongles |
| `probe <MHz> [s]` | stream and report rate / level / clipping |
| `capture <MHz> <s> <out.cu8> [gain dB]` | record IQ at 1,488,375 S/s (nrsc5's cu8 format) |
| `spectrum <file.cu8>` | averaged spectrum across ±375 kHz |
| `scan [gain dB]` | sweep 88–108 MHz and list the strongest stations |
| `fm <in.cu8> <out.wav>` | demodulate a recording (analog only) to a 48 kHz stereo WAV, with pilot/blend stats |
| `hd <in.cu8> <out.wav>` | full chain on a recording: HD decode, alignment, blend, L/R orientation check |
| `play <MHz> [gain dB] [s]` | the radio, live: ←/→ tune, 1–8 HD program, A analog only, E equalizer, M mono, Q quit |
| `selftest [CNR dB] [echo dB] [echo µs]` | synthetic stereo broadcast through the receiver: response, separation, residual |
| `compare`, `snr`, `gaintest` | audio comparison and gain diagnostics |

## How the FM receiver works

All stages run at integer fractions of the dongle's clock, so the analog audio and the HD baseband share one timeline:

| Stage | Rate (S/s) | |
|---|---|---|
| Halfband ÷2 | 1,488,375 → 744,187.5 | HD baseband (nrsc5's native rate) |
| Channel filter ÷2 | → 372,094 | ±100 kHz; rejects the HD sidebands and neighbours |
| FM discriminator | 372,094 | → MPX |
| MPX filter ÷2 | → 186,047 | 0–60 kHz: mono, pilot, stereo, RDS |
| Stereo decoder ÷4 | → 46,512 | 19 kHz pilot PLL, L−R demod, stereo blend by pilot SNR, 75 µs de-emphasis |
| Resampler | → 48,000 | trimmed by a PI loop on the output buffer level to absorb dongle/soundcard clock drift |

Audio goes out through WASAPI shared mode and follows the Windows default device.

A blind constant-modulus equalizer on the FM channel removes multipath (FM has a constant envelope; reflections
make it ripple).

## How HD works

The 744,187.5 S/s baseband goes straight into [nrsc5](https://github.com/theori-io/nrsc5) (pipe mode). Its audio
arrives about 2.5 s ahead of the analog; the blender measures the exact offset by cross-correlating loudness
envelopes, then plays the HD sample that matches what the analog is saying, so switching is seamless. Unavailable HD
frames stay in the timeline as gaps, the blender crossfades to analog just before them, and after dropouts it waits
for longer and longer clean runs (2, 4, 8 … 30 s) before going back to HD. HD2+ aren't simulcast, so they play from
a short buffer.

nrsc5's HD stereo comes out mirrored relative to the analog on every station tested, so the blender swaps it
(`HdBlender.SwapHdChannels`).

## Credits

- [nrsc5](https://github.com/theori-io/nrsc5) (GPLv3): HD Radio decoding
- [librtlsdr](https://gitea.osmocom.org/sdr/rtl-sdr) (GPLv2+) with [libusb](https://libusb.info/) (LGPL 2.1)
- [NAudio](https://github.com/naudio/NAudio) (MIT): WASAPI output

## License

GPL-3.0-or-later. Not affiliated with Xperi (HD Radio).
