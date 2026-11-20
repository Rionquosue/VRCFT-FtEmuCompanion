# Third-party notices

## MediaPipe Face Landmarker models (`Models/face_*.onnx`)

Converted to ONNX, unchanged otherwise, from MediaPipe's `face_landmarker.task` (float16, version 1). Source, file hashes and the conversion are recorded in `Models/mediapipe.json`.

Copyright Google LLC. Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0

## ONNX Runtime (the module)

Microsoft.ML.OnnxRuntime, Copyright Microsoft Corporation. MIT License: https://github.com/microsoft/onnxruntime/blob/main/LICENSE

## OpenVR (`ThirdParty/OpenVR/openvr_api.dll`, the Recorder's Start with SteamVR)

`openvr_api.dll` from Valve's OpenVR SDK 2.15.6 (`bin/win64`, SHA-256 `bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a`), unchanged. Source: https://github.com/ValveSoftware/openvr

```
Copyright (c) 2015, Valve Corporation
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
this list of conditions and the following disclaimer in the documentation and/or
other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
may be used to endorse or promote products derived from this software without
specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Compiled builds (`RiryTools-FTRecorder.exe`, `RiryTools-FTRecorder-x86_64.AppImage`)

Made with PyInstaller (GPL 2.0 with an exception that allows distributing the programs it makes under any licence). They contain:

- Python (Python Software Foundation License): https://docs.python.org/3/license.html
- Tcl/Tk (BSD-style Tcl/Tk license), for tkinter: https://www.tcl-lang.org/software/tcltk/license.html
- numpy (BSD 3-Clause), with the OpenBLAS it ships (BSD 3-Clause): https://github.com/numpy/numpy/blob/main/LICENSE.txt
- onnxruntime (MIT): https://github.com/microsoft/onnxruntime/blob/main/LICENSE
- The MediaPipe models and OpenVR above.
- The Linux AppImage also carries the shared libraries PyInstaller collects from the build system (such as libX11, libXft, fontconfig, freetype, libpng, libffi, expat, brotli, OpenSSL, zlib, bzip2, xz, and the GCC runtime libraries under the GCC Runtime Library Exception), each under its own licence. GNU readline (GPL) is left out.

## Not included

The Babble face model, OVRLipSync and ffmpeg are not part of this project. They are loaded from paths you set (OVRLipSync, by default, from your VRChat install), under their own licences. Python, numpy (BSD 3-Clause) and onnxruntime (MIT) for the Python apps are installed by you (`requirements.txt`).
