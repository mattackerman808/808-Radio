# 808 Radio on macOS

The port keeps `Radio808.Core` (the receiver, HD decoding, the network dongle, discovery) as one cross-platform C#
library and replaces only what is Windows-specific: the audio output and the user interface. Everything the app
needs ships inside it, signed and notarized, so a user installs nothing else.

## Status

| | |
|---|---|
| Native libraries for Apple Silicon | **done**: `native/mac/build-native-mac.sh` builds `libnrsc5.dylib` (nrsc5 with FFTW, FAAD2, librtlsdr and libusb linked in) and `libr808audio.dylib` (miniaudio over CoreAudio). Both depend only on system frameworks. |
| nrsc5 event layout on arm64 | **verified** by `native/mac/check_offsets.c` against the offsets in `Nrsc5Native.cs` / `HdDecoder.cs` |
| Core builds for `net9.0` | **done**: multi-targets `net9.0` (miniaudio) and `net9.0-windows` (WASAPI, used by the Windows app) |
| Console radio on the Mac (`radio808-tools play`, network dongle) | **done**: 2026-10-02, HD1 audio from a Pi 4 over Ethernet (KBAY 98.5, MER 11-12 dB, ~24 Mb/s), discovery finds the Pi by mDNS |
| Mac GUI (`src/Radio808.Avalonia`) | **at parity** (2026-10-02) except where noted: faceplate, flip-down panel, menu with frame rate, shortcuts, edge resize, single instance, animated art pop-out, weather radar over a street map, live traffic mosaic. Not yet: media keys (untested), the `.app` bundle and signing. Note: the Pi's Avahi advertisement is a static file, so "rtl_tcp on console" stays in the Source menu even when the Pi has no dongle. |
| Signed, notarized `.app` | not started (phase 2) |
| USB dongle on the Mac (`librtlsdr.dylib`) | **done** (2026-10-02): libusb linked in statically, no driver needed on macOS, verified playing from an RTL-SDR Blog V3 on the Mac |

## Building (developer machine)

Tools (none are shipped): Xcode command line tools, and `brew install cmake autoconf automake libtool pkg-config`,
plus the .NET 9 SDK (`curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0`, which installs to
`~/.dotnet`).

```
git submodule update --init
native/mac/build-native-mac.sh          # first time: several minutes; downloads and builds FFTW, libusb, rtl-sdr, FAAD2
dotnet build tools/Radio808.Tools -c Release
R808_RTLTCP=<pi host> dotnet tools/Radio808.Tools/bin/Release/net9.0/radio808-tools.dll play 97.3
dotnet tools/Radio808.Tools/bin/Release/net9.0/radio808-tools.dll discover    # rtl_tcp servers on the network
```

`-Clean` rebuilds the native libraries. Build the app in Release for real use: Debug costs about 10 points of CPU.
Playing from the Pi with the faceplate up, the Release app uses about 45% of one core (the receiver's FIR filters
vectorize to 128-bit on Apple Silicon; the analyzer square is its own visual so its 60 fps frames repaint nothing else). nrsc5's own FFTW build passes x86 flags, so the script builds FFTW itself
(NEON on arm64); rtl-sdr's static library is `librtlsdr.a` on macOS where nrsc5 expects `librtlsdr_static.a`, and the
static libusb needs CoreFoundation, IOKit and Security, which the script adds to the link.

## How the pieces map

| Windows | macOS |
|---|---|
| `AudioPlayer` on NAudio / WASAPI | the same `AudioPlayer` (ring buffer, clock-drift loop) on `IAudioDevice`: `WasapiAudioDevice` (Windows) or `MiniAudioDevice` → `libr808audio.dylib` → CoreAudio; both follow the system default device |
| `libnrsc5.dll` (MSYS2) | `libnrsc5.dylib` (clang), same P/Invoke; `.NET` resolves `libnrsc5` to the right file name per OS |
| `rtlsdr.dll` for USB dongles | not built yet; `RtlSdrDevice` is only loaded when a USB dongle is used |
| mDNS discovery (`RtlTcpDiscovery`) | unchanged: it sends legacy-unicast queries from an ordinary port, so it needs no port 5353 on macOS either |
| WinForms + GDI+ + Direct2D | phase 2 (below) |

## Developing the Mac app

```
dotnet build src/Radio808.Avalonia -c Debug
dotnet src/Radio808.Avalonia/bin/Debug/net9.0/808Radio.dll              # run
dotnet src/Radio808.Avalonia/bin/Debug/net9.0/808Radio.dll --snapshot face.png 10   # run 10 s, save the faceplate (2x PNG), exit
```

`src/Radio808.Shared` holds what both shells use (`RadioController`, `AppSettings`, `AppLog`); the Windows app
references it too, and compiles on macOS with `dotnet build src/Radio808.App -p:EnableWindowsTargeting=true` (it
can't run here, but a change to the shared code is checked against both).

Drawing: `FaceplateControl` is `FaceplateView` line for line on Avalonia's `DrawingContext`, through the helpers in
`Drawing/G.cs` (arcs by angle, rounded rectangles, labels in a rectangle) so the two stay comparable; `NerdPanel` is
the Windows `NerdPanel` on `IPanelCanvas` (`Drawing/PanelCanvas.cs`), one implementation for everything (Avalonia
renders through Skia on the GPU, so there is no separate Direct2D path). `DotMatrix` uses a built-in 5 x 8 glyph table
instead of sampling Consolas.

**Never `PushClip` on the drawing context.** On Avalonia 12.1 / Skia, after a clip is popped the backend reads the
canvas matrix back *including the DPI scale*, so the next relative transform push, which text drawing does
internally, applies the DPI scale twice: everything after the first text is drawn at 2x on a Retina display (seen
2026-10-02, bisected with `--snapshot`). So the design scale is a `RenderTransform` on the control, the hatch is a
tiled `DrawingBrush`, the album art an `ImageBrush` fill, and the sliding spectrum + waterfall is a child visual with
`ClipToBounds` (`SpectrumView`), which the compositor clips without the bug.

## The weather map

HD Radio's weather image (HERE Images) is a radar layer only: a 600 x 600 transparent PNG whose corners the
broadcast gives as latitude/longitude (north/west and south/east edges; nrsc5's `here_image` event, now kept in
`HdStatus.WeatherBounds`). It covers equal spans of latitude and longitude, so it is a plain lat/lon rectangle, not
Mercator. The traffic map is different: nine complete 200 x 200 map tiles, shown as a mosaic.

The Mac app draws the radar over a street map of the same box (`Map/`): `PmTiles` reads Swiftcamp's basemap archive
(`cdn.swiftcamp.app/street-z15-20260910.pmtiles`, Protomaps schema, OpenStreetMap data, gzip) by HTTP byte range and
caches tiles under `~/Library/Application Support/808Radio/maps`; `Mvt` decodes the vector tiles; `BaseMap` renders
land use, water, roads, boundaries and place names in Swiftcamp's palette into a bitmap (cached as a PNG per box and
size); `MapWindow` puts the radar on top, placed by its corners (over a one-degree box the plate-carrée-on-Mercator
error is under a pixel), with the time and the ODbL attribution. Offline, the radar shows alone.

`808Radio --mapsnap radar.png N W S E out.png [seconds]` renders a saved radar image over its map without the radio;
`--mvtdump tile.mvt` lists a cached tile's layers and tags. The console tool saves a station's images with
`R808_SAVE_DIR=<folder> radio808-tools play 96.5 auto 150` (KOIT 96.5 sends weather and traffic every couple of minutes).

## Phase 2: the Mac app

Goal: a native-feeling `.app` that can be distributed outside the Mac App Store (Developer ID + notarization), and
inside it if the licensing allows (808 Radio, nrsc5 and FAAD2 are GPL; the App Store terms are generally held to be
incompatible with the GPL, and nrsc5's copyright isn't ours to relicense).

Recommended: **Avalonia** (C#), reusing `RadioController` as-is and redrawing the faceplate and instrument panel on a
Skia canvas; one shell for macOS (and Linux), the WinForms app stays the Windows build until the Avalonia one is at
parity. Steps:

1. `src/Radio808.Mac` (or `Radio808.Avalonia`): Avalonia app, `RadioController` moved to Core or a shared project
   (it only needs a `SynchronizationContext`), settings in `~/Library/Application Support/808Radio/settings.json`.
2. Faceplate: port `FaceplateView`, `DotMatrix`, `SevenSegment`, `ArtPopup` from GDI+ to Avalonia's `DrawingContext`
   / Skia (the drawing is mostly fills, paths and text; the animations run off a timer already).
3. Instrument panel: `NerdPanel` on a Skia canvas (Avalonia renders on the GPU through Skia; no Direct2D port needed).
4. Bundle: `dotnet publish -r osx-arm64 --self-contained` into `808 Radio.app/Contents/MacOS`, the dylibs in
   `Contents/MacOS` next to the executable (that's where .NET probes), `Info.plist` with
   `NSLocalNetworkUsageDescription` and `NSBonjourServices` = `_rtl-tcp._tcp` (macOS 15+ asks before an app may use
   the local network), an `.icns` from `docs/images/icon.png`.
5. Sign with hardened runtime (`com.apple.security.cs.allow-jit` for the .NET JIT) and notarize; for the App Store
   also the sandbox with `com.apple.security.network.client` (and `device.usb` later for a USB dongle).
6. Universal binary (arm64 + x86_64): publish both RIDs and `lipo` the native libraries; optional, Apple Silicon first.

Alternative considered: a Swift rewrite. The DSP is ~3,700 lines tuned against real stations; porting it means a
second implementation to keep in step with Windows. The recordings and offline `fm` / `hd` tools would be the
regression harness if that is ever wanted.
