# Third-party components

- NAudio 2.2.1 — Copyright Mark Heath and contributors, MIT. https://github.com/naudio/NAudio/tree/v2.2.1
- SoundTouch.Net 2.3.2 — Copyright Olli Parviainen; C# port copyright Olaf Woudenberg, LGPL-2.1-or-later. Used without modifications. Corresponding source: https://github.com/owoudenberg/soundtouch.net/tree/98e5b8fd2f8efed0ddf7c8f66b435bfb231659dc
- Microsoft .NET Windows Desktop runtime — Microsoft and contributors; distributed by the official self-contained .NET publish toolchain. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

License copies for NAudio and SoundTouch are in `licenses/`. The application source and build files permit rebuilding/relinking with a modified SoundTouch.Net library: replace its package reference with a compatible project or assembly reference, then run `build.cmd`. No restriction on reverse engineering for debugging modifications of the LGPL component is imposed. Rebuilt EXEs do not carry the original publisher's update signature.
