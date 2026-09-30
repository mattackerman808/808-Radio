# 808 Radio

A standalone FM receiver for Windows with HD Radio (NRSC-5) built in, for RTL-SDR dongles.
Car-radio style: tune, seek, presets, HD1–HD8, now-playing with album art.

Sister project of [808 HD](https://github.com/mattackerman808/808-HD), the HD Radio plugin for SDR#.

> Work in progress. Currently: device access and IQ capture tools. Next: analog FM, then HD.

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

## Credits

- [nrsc5](https://github.com/theori-io/nrsc5) (GPLv3): HD Radio decoding
- [librtlsdr](https://gitea.osmocom.org/sdr/rtl-sdr) (GPLv2+) with [libusb](https://libusb.info/) (LGPL 2.1)

## License

GPL-3.0-or-later. Not affiliated with Xperi (HD Radio).
