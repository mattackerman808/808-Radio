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
| [.NET runtime and Windows Forms](https://github.com/dotnet) | Runtime (inside `808Radio.exe`) | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/winforms |

`libusb-1.0.dll` is shipped as a separate DLL so it can be replaced. Everything statically linked can be rebuilt
from the source repository with `build.ps1`.

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
