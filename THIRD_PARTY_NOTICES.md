# Third-party notices

808 Radio is licensed under the GNU General Public License v3.0 or later (see `LICENSE`). It includes the
following components. Their complete source code, and the scripts used to build them, are in the 808 Radio source
repository: https://github.com/mattackerman808/808-Radio (the `nrsc5` and `rtl-sdr` submodules, plus
`build-native.sh`).

| Component | Used for | License | Source |
|---|---|---|---|
| [nrsc5](https://github.com/theori-io/nrsc5) | HD Radio (NRSC-5) decoding (`libnrsc5.dll`) | GPL-3.0-or-later | https://github.com/theori-io/nrsc5 |
| [FAAD2](https://github.com/knik0/faad2), patched by nrsc5 for HDC | HD Radio audio decoding (inside `libnrsc5.dll`) | GPL-2.0-or-later | https://github.com/knik0/faad2 |
| [FFTW 3](https://www.fftw.org/) | FFTs in nrsc5 (inside `libnrsc5.dll`) | GPL-2.0-or-later | https://www.fftw.org/ |
| [librtlsdr](https://gitea.osmocom.org/sdr/rtl-sdr) (osmocom) | RTL-SDR dongle access (`rtlsdr.dll`) | GPL-2.0-or-later | https://gitea.osmocom.org/sdr/rtl-sdr |
| [libusb](https://libusb.info/) | USB access (`libusb-1.0.dll`; also linked statically inside `libnrsc5.dll`) | LGPL-2.1-or-later | https://github.com/libusb/libusb |
| [NAudio](https://github.com/naudio/NAudio) | WASAPI audio output (inside `808Radio.exe`) | MIT | https://github.com/naudio/NAudio |
| [miniaudio](https://miniaud.io/) | CoreAudio audio output on macOS (`libr808audio.dylib`) | MIT-0 (or public domain) | https://github.com/mackron/miniaudio |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct2D, Direct3D 11, DXGI) and [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) | GPU drawing of the instrument panel (inside `808Radio.exe`) | MIT | https://github.com/amerkoleci/Vortice.Windows |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGenTools) | COM interop for Vortice (inside `808Radio.exe`) | MIT | https://github.com/SharpGenTools/SharpGenTools |
| [.NET runtime and Windows Forms](https://github.com/dotnet) | Runtime (inside `808Radio.exe`) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/winforms |

`libusb-1.0.dll` is shipped as a separate DLL so it can be replaced. Everything statically linked can be rebuilt
from the source repository with `build.ps1`. On macOS, `libnrsc5.dylib` contains nrsc5, FAAD2, FFTW, librtlsdr and
libusb, built by `native/mac/build-native-mac.sh`; `libr808audio.dylib` is miniaudio with a small shim
(`native/mac/r808audio.c`).

## Trademarks

HD Radio is a trademark of Xperi Inc. RTL-SDR Blog is a trademark of its owners. 808 Radio is not affiliated with or
endorsed by Xperi, RTL-SDR Blog, Osmocom, or the authors of the components above. The 808 Radio mark is original
artwork.

## MIT License (NAudio)

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MIT No Attribution (miniaudio)

Copyright 2025 David Reid

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MIT License (.NET)

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MIT License (Vortice.Windows, Vortice.Mathematics)

Copyright (c) Amer Koleci and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## MIT License (SharpGen.Runtime)

Copyright (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
