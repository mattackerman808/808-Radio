#!/bin/bash
# Builds the native libraries for macOS (Apple Silicon), self-contained for distribution: everything the app needs is
# linked statically into two dylibs that depend only on system frameworks, so users install nothing.
#
#   native/bin/libnrsc5.dylib     HD Radio decoder (nrsc5 submodule) with FFTW, FAAD2, librtlsdr and libusb inside
#   native/bin/libr808audio.dylib the audio output shim (miniaudio over CoreAudio), see r808audio.c
#
# Developer-machine tools (not shipped): Xcode command line tools, cmake, autoconf, automake, libtool, pkg-config
# (brew install cmake autoconf automake libtool pkg-config). First build: several minutes (it downloads and builds
# FFTW, libusb, rtl-sdr and FAAD2). Pass -Clean to rebuild everything.
set -euo pipefail
cd "$(dirname "$0")/../.."
root=$(pwd)
out="$root/native/bin"
prefix="$root/native/mac/prefix"       # static third-party libraries (gitignored)
export MACOSX_DEPLOYMENT_TARGET=${MACOSX_DEPLOYMENT_TARGET:-13.0}   # .NET 9's minimum
arch=$(uname -m)
jobs=$(sysctl -n hw.ncpu)
mkdir -p "$out" "$prefix"

if [ "${1:-}" = "-Clean" ]; then rm -rf "$prefix" nrsc5/build-mac "$out"/*.dylib; mkdir -p "$prefix"; fi

# ---- FFTW (single precision, static, NEON). nrsc5's own fftw build passes x86 flags (--enable-avx), so build it here.
if [ ! -f "$prefix/lib/libfftw3f.a" ]; then
    echo "== FFTW"
    mkdir -p "$prefix/src" && cd "$prefix/src"
    [ -f fftw-3.3.10.tar.gz ] || curl -fsSL -o fftw-3.3.10.tar.gz https://www.fftw.org/fftw-3.3.10.tar.gz
    rm -rf fftw-3.3.10 && tar xzf fftw-3.3.10.tar.gz && cd fftw-3.3.10
    simd=""; [ "$arch" = "arm64" ] && simd="--enable-neon"; [ "$arch" = "x86_64" ] && simd="--enable-sse2 --enable-avx --enable-avx2"
    ./configure --prefix="$prefix" --enable-float --enable-static --disable-shared --disable-fortran --with-our-malloc $simd \
        CFLAGS="-O3 -fPIC" > configure.log
    make -j"$jobs" > make.log && make install > install.log
    cd "$root"
fi

# ---- libnrsc5.dylib (FAAD2, librtlsdr and libusb built from source by nrsc5's CMake and linked statically)
if [ ! -f "$out/libnrsc5.dylib" ]; then
    echo "== libnrsc5"
    cmake -S nrsc5 -B nrsc5/build-mac -DCMAKE_BUILD_TYPE=Release -DBUILD_CLI=OFF \
        -DUSE_SYSTEM_FFTW=ON -DFFTW3F_LIBRARIES="$prefix/lib/libfftw3f.a" -DFFTW3F_INCLUDE_DIRS="$prefix/include" \
        -DUSE_SYSTEM_RTLSDR=OFF -DUSE_SYSTEM_LIBUSB=OFF \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" -DCMAKE_C_FLAGS="-fPIC" \
        -DCMAKE_SHARED_LINKER_FLAGS="-framework CoreFoundation -framework IOKit -framework Security"   # for the static libusb
    # the bundled libraries first: rtl-sdr's static library is librtlsdr.a on macOS, but nrsc5 expects librtlsdr_static.a
    cmake --build nrsc5/build-mac -j"$jobs" --target rtlsdr_external faad2_external
    [ -f nrsc5/build-mac/rtlsdr-prefix/lib/librtlsdr_static.a ] || ln -s librtlsdr.a nrsc5/build-mac/rtlsdr-prefix/lib/librtlsdr_static.a
    cmake --build nrsc5/build-mac -j"$jobs" --target nrsc5
    cp nrsc5/build-mac/src/libnrsc5.dylib "$out/libnrsc5.dylib"
    install_name_tool -id @rpath/libnrsc5.dylib "$out/libnrsc5.dylib"
fi

# ---- librtlsdr.dylib: USB dongles on the Mac (osmocom librtlsdr from the submodule, libusb linked in statically)
if [ ! -f "$out/librtlsdr.dylib" ]; then
    echo "== librtlsdr"
    usb="$root/nrsc5/build-mac/libusb-prefix"   # built by nrsc5's CMake above
    # rtl-sdr finds libusb through pkg-config, which would link the shared libusb next to the static one: hand it a
    # .pc that names the static archive, so the dylib depends only on system frameworks
    mkdir -p "$prefix/pkgconfig"
    cat > "$prefix/pkgconfig/libusb-1.0.pc" <<PC
Name: libusb-1.0
Description: static libusb for 808 Radio
Version: 1.0
Libs: $usb/lib/libusb-1.0.a -framework CoreFoundation -framework IOKit -framework Security -lobjc
Cflags: -I$usb/include/libusb-1.0
PC
    # (CMake's pkg-config import keeps only -l flags, so the archive goes on the link line itself)
    PKG_CONFIG_PATH="$prefix/pkgconfig" cmake -S rtl-sdr -B rtl-sdr/build-mac -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" -DDETACH_KERNEL_DRIVER=OFF -DINSTALL_UDEV_RULES=OFF \
        -DCMAKE_SHARED_LINKER_FLAGS="$usb/lib/libusb-1.0.a -framework CoreFoundation -framework IOKit -framework Security -lobjc"
    cmake --build rtl-sdr/build-mac -j"$jobs" --target rtlsdr
    cp rtl-sdr/build-mac/src/librtlsdr.*.dylib "$out/librtlsdr.dylib" 2>/dev/null || cp rtl-sdr/build-mac/src/librtlsdr.dylib "$out/librtlsdr.dylib"
    install_name_tool -id @rpath/librtlsdr.dylib "$out/librtlsdr.dylib"
fi

# ---- libr808audio.dylib (always rebuilt: it's one small file)
echo "== libr808audio"
clang -O2 -dynamiclib -fvisibility=hidden -mmacosx-version-min="$MACOSX_DEPLOYMENT_TARGET" \
    -install_name @rpath/libr808audio.dylib \
    native/mac/r808audio.c -o "$out/libr808audio.dylib" \
    -framework CoreFoundation -framework CoreAudio -framework AudioUnit -framework AudioToolbox -lpthread -lm

echo "Native libraries in native/bin (should depend only on /usr/lib and /System frameworks):"
for f in "$out"/*.dylib; do echo "  $(basename "$f"):"; otool -L "$f" | tail -n +2 | sed 's/^/     /'; done
