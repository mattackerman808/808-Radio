#!/bin/bash
# Builds the native libraries with MSYS2 (UCRT64): rtlsdr.dll (osmocom librtlsdr) and libnrsc5.dll.
# Called by build.ps1. Output: native/bin/*.dll
set -euo pipefail
cd "$(dirname "$0")"
root=$(pwd)
out="$root/native/bin"
mkdir -p "$out"

# ---- rtlsdr.dll (+ libusb-1.0.dll, LGPL, shipped as a separate DLL) ----
cp "$MINGW_PREFIX/bin/libusb-1.0.dll" "$out/"
if [ ! -f "$out/rtlsdr.dll" ]; then
    rm -rf rtl-sdr/build && mkdir -p rtl-sdr/build && cd rtl-sdr/build
    cmake .. -G "MSYS Makefiles" -DCMAKE_BUILD_TYPE=Release \
        -DLIBUSB_LIBRARIES="$MINGW_PREFIX/lib/libusb-1.0.a" \
        -DLIBUSB_INCLUDE_DIRS="$MINGW_PREFIX/include/libusb-1.0" \
        -DCMAKE_SHARED_LINKER_FLAGS="-static-libgcc -Wl,-Bstatic -lwinpthread -Wl,-Bdynamic"
    make -j"$(nproc)" rtlsdr
    cp src/librtlsdr.dll "$out/rtlsdr.dll" 2>/dev/null || cp src/rtlsdr.dll "$out/rtlsdr.dll"
    cd "$root"
fi

# ---- libnrsc5.dll ----
if [ ! -f "$out/libnrsc5.dll" ]; then
    rm -rf nrsc5/build && mkdir -p nrsc5/build && cd nrsc5/build
    cmake .. -G "MSYS Makefiles" -DUSE_STATIC=ON -DUSE_SSE=ON \
        -DUSE_SYSTEM_LIBUSB=OFF -DUSE_SYSTEM_RTLSDR=OFF -DUSE_SYSTEM_LIBAO=OFF -DUSE_SYSTEM_FFTW=OFF
    make -j"$(nproc)"
    cp src/libnrsc5.dll "$out/libnrsc5.dll"
    cd "$root"
fi

echo "Native DLL imports:"
for f in "$out"/*.dll; do echo "  $(basename "$f"): $(objdump -p "$f" | grep 'DLL Name' | awk '{print $3}' | tr '\n' ' ')"; done
