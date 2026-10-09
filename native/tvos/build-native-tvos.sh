#!/bin/bash
# Builds libnrsc5.a for tvOS: the HD Radio decoder (nrsc5 submodule) with FFTW and the HDC-patched FAAD2 inside, for
# the Apple TV (arm64) and the simulator (arm64), as static archives the .NET tvOS app links in.
#
#   native/tvos/lib/appletvos/libnrsc5.a
#   native/tvos/lib/appletvsimulator/libnrsc5.a
#
# nrsc5's USB input needs librtlsdr; the app only ever feeds samples from rtl_tcp, so a stub stands in (rtlsdr_stub.c).
# Needs Xcode with the tvOS platform, cmake, and git. "-Clean" rebuilds everything.
set -euo pipefail
root=$(cd "$(dirname "$0")/../.." && pwd)
work="$root/native/tvos/build"
out="$root/native/tvos/lib"
min=17.0
jobs=$(sysctl -n hw.ncpu)
hash=$(git -C "$root/nrsc5" rev-parse --short HEAD)
[ "${1:-}" = "-Clean" ] && rm -rf "$work" "$out"
mkdir -p "$work" "$out"

# ---- sources: FFTW tarball, FAAD2 with nrsc5's HD codec patch
if [ ! -d "$work/fftw-3.3.10" ]; then
    echo "== FFTW source"
    curl -fsSL -o "$work/fftw.tar.gz" https://www.fftw.org/fftw-3.3.10.tar.gz
    tar xzf "$work/fftw.tar.gz" -C "$work"
fi
if [ ! -d "$work/faad2" ]; then
    echo "== FAAD2 source (2.11.2 + nrsc5's HDC patch)"
    git clone -q --depth 1 --branch 2.11.2 https://github.com/knik0/faad2.git "$work/faad2"
    (cd "$work/faad2" && patch -p1 -Ni "$root/nrsc5/support/faad2-hdc-support.patch")
fi

# nrsc5's config.h (what its CMake probes on Darwin)
mkdir -p "$work/config"
cat > "$work/config/config.h" <<'H'
#pragma once
#define USE_FAAD2
#define HAVE_FAAD2
#define HAVE_STRNDUP
#define HAVE_CMPLXF
#define HAVE_COMPLEX_I
#define LIBRARY_DEBUG_LEVEL 5
H

build_platform() {
    local p=$1 target=$2
    local sdk; sdk=$(xcrun --sdk "$p" --show-sdk-path)
    local cc; cc=$(xcrun --sdk "$p" -f clang)
    local cflags="-isysroot $sdk -target $target -O3 -fPIC"
    local w="$work/$p"
    mkdir -p "$w" "$out/$p"

    if [ ! -f "$w/fftw/lib/libfftw3f.a" ]; then
        echo "== FFTW ($p)"
        rm -rf "$w/fftw-build" && cp -R "$work/fftw-3.3.10" "$w/fftw-build"
        (cd "$w/fftw-build" && ./configure --host=aarch64-apple-darwin --prefix="$w/fftw" \
            --enable-float --enable-static --disable-shared --disable-fortran --with-our-malloc --enable-neon \
            CC="$cc" CFLAGS="$cflags" > configure.log && make -j"$jobs" > make.log && make install > install.log)
    fi

    if [ ! -f "$w/faad2/libfaad_hdc.a" ]; then
        echo "== FAAD2 ($p)"
        cmake -S "$work/faad2" -B "$w/faad2" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DFAAD_BUILD_CLI=OFF \
            -DCMAKE_SYSTEM_NAME=tvOS -DCMAKE_OSX_SYSROOT="$p" -DCMAKE_OSX_ARCHITECTURES=arm64 \
            -DCMAKE_OSX_DEPLOYMENT_TARGET="$min" -DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY \
            -DCMAKE_C_FLAGS="-fPIC" > "$w/faad2-configure.log"
        cmake --build "$w/faad2" -j"$jobs" --target faad_hdc > "$w/faad2-build.log"
    fi

    echo "== nrsc5 ($p)"
    rm -rf "$w/nrsc5" && mkdir -p "$w/nrsc5"
    local inc="-I$work/config -I$root/nrsc5/include -I$w/fftw/include -I$work/faad2/include -I$root/rtl-sdr/include"
    for f in acquire decode frame here_images input nrsc5 output pids rtltcp sync firdecim_cf32 conv_dec rs_init rs_decode unicode strndup; do
        "$cc" $cflags -std=gnu11 -D_GNU_SOURCE -DNRSC5_EXPORTS=1 -DGIT_COMMIT_HASH="\"$hash\"" -Wall $inc -c "$root/nrsc5/src/$f.c" -o "$w/nrsc5/$f.o"
    done
    "$cc" $cflags -std=gnu11 $inc -c "$root/native/tvos/rtlsdr_stub.c" -o "$w/nrsc5/rtlsdr_stub.o"
    xcrun --sdk "$p" ar rcs "$w/nrsc5/libnrsc5-core.a" "$w"/nrsc5/*.o
    # one archive with everything in it
    xcrun --sdk "$p" libtool -static -o "$out/$p/libnrsc5.a" \
        "$w/nrsc5/libnrsc5-core.a" "$w/fftw/lib/libfftw3f.a" "$w/faad2/libfaad_hdc.a"
    echo "   $out/$p/libnrsc5.a: $(du -h "$out/$p/libnrsc5.a" | cut -f1)"
}

build_platform appletvos "arm64-apple-tvos$min"
build_platform appletvsimulator "arm64-apple-tvos$min-simulator"
echo "done"
