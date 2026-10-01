# 808 Radio

A standalone FM receiver for Windows with HD Radio (NRSC-5) built in, for RTL-SDR dongles.
Car-radio style: tune, seek, presets, HD1–HD8, now-playing with album art.

Sister project of [808 HD](https://github.com/mattackerman808/808-HD), the HD Radio plugin for SDR#.

> Work in progress. Currently: analog FM stereo, playable from the command line (`radio808-tools play`). Next: HD.

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
| `fm <in.cu8> <out.wav>` | demodulate a recording to a 48 kHz stereo WAV, with pilot/blend stats |
| `play <MHz> [gain dB] [s]` | listen live through the default audio device |

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

## Credits

- [nrsc5](https://github.com/theori-io/nrsc5) (GPLv3): HD Radio decoding
- [librtlsdr](https://gitea.osmocom.org/sdr/rtl-sdr) (GPLv2+) with [libusb](https://libusb.info/) (LGPL 2.1)
- [NAudio](https://github.com/naudio/NAudio) (MIT): WASAPI output

## License

GPL-3.0-or-later. Not affiliated with Xperi (HD Radio).
