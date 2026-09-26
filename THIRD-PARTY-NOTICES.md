# Third-party components

DialShift is released as two self-contained packages built from the single `DialShift.App` project, and the Windows package also as a setup. Each package contains only the components listed for it below. All third-party software components are unmodified and dynamically linked. Both packages also carry station data compiled from third-party sources, described under [Station catalog data](#station-catalog-data-app-catalogjson). There is no Intel Mac package.

| Package | Artifact | Playback |
|---|---|---|
| Windows (`win-x64`) | `DialShift-win-x64.zip` | LibVLC (bundled) |
| Windows setup (`win-x64`) | `DialShift-Setup-win-x64.exe` (installs the Windows package) | LibVLC (bundled) |
| macOS Apple Silicon (`osx-arm64`) | `DialShift-osx-arm64-native-avplayer.zip` (`DialShift.app`) | Apple AVFoundation / AVPlayer (part of macOS, not bundled) |

## Both packages

- **Avalonia 12.1.2** (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` and the platform assemblies they bring) — MIT. [Website](https://avaloniaui.net/), [source](https://github.com/AvaloniaUI/Avalonia/tree/12.1.2).
- **SkiaSharp 3.119.4** and **HarfBuzzSharp 8.3.1.3** — MIT managed bindings plus the native Skia and HarfBuzz libraries, which carry their own component licenses (see `SkiaSharp-THIRD-PARTY-NOTICES.txt`). [Source](https://github.com/mono/SkiaSharp).
- **MicroCom.Runtime 0.11.6** — MIT. [Source](https://github.com/kekekeks/MicroCom).
- **Tmds.DBus.Protocol 0.94.1** — MIT. A managed Avalonia dependency for Linux; unused on Windows and macOS. [Source](https://github.com/tmds/Tmds.DBus).
- **Microsoft.Extensions.DependencyInjection 10.0.12** and **Microsoft.Win32.SystemEvents 10.0.12** — MIT, part of .NET. [Source](https://github.com/dotnet/runtime).
- **.NET 10 runtime** (self-contained) — Microsoft and contributors, MIT and component notices (`NET-LICENSE.txt`, `NET-THIRD-PARTY-NOTICES.txt`). [Source](https://github.com/dotnet/runtime).
- **LibVLCSharp 3.10.1** — LGPL-2.1-or-later. The managed `LibVLCSharp.dll` is present in both packages because the project references it at compile time. Only the Windows package loads it; the macOS package contains no VLC runtime for it to bind to. [Project and source](https://code.videolan.org/videolan/LibVLCSharp), [NuGet](https://www.nuget.org/packages/LibVLCSharp/3.10.1).

## Windows package only

- **VideoLAN.LibVLC.Windows 3.0.23.1** — the VLC/LibVLC native runtime and plugins, in `libvlc/win-x64`. LibVLC is LGPL-2.1-or-later; the bundled plugins and their dependencies have individual licenses, including GPL components. [Packaging/source instructions](https://code.videolan.org/videolan/libvlc-nuget), [VLC source](https://code.videolan.org/videolan/vlc/-/tree/3.0.23), [VideoLAN legal information](https://www.videolan.org/legal.html).
- **ANGLE** (`av_libglesv2.dll`, from `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419) — BSD-3-Clause, The ANGLE Project Authors. Avalonia's OpenGL ES rendering on Windows. [Source](https://github.com/AvaloniaUI/angle).
- Native Skia and HarfBuzz for Windows (`libSkiaSharp.dll`, `libHarfBuzzSharp.dll`).

The native VLC libraries stay separate in `libvlc/win-x64`, so a compatible replacement can be supplied.

## macOS package only

- Playback uses **Apple AVFoundation (AVPlayer)**, which is part of macOS and linked from the system. It is not bundled. **The macOS package contains no VLC or LibVLC native libraries.**
- Native Avalonia, Skia and HarfBuzz for macOS (`libAvaloniaNative.dylib`, `libSkiaSharp.dylib`, `libHarfBuzzSharp.dylib`).

## Windows setup only

`DialShift-Setup-win-x64.exe` installs the Windows package above: everything listed under [Both packages](#both-packages), [Windows package only](#windows-package-only) and the [station catalog data](#station-catalog-data-app-catalogjson), except `Install.ps1`. It adds:

- **NSIS 3.12** (Nullsoft Scriptable Install System, [website](https://nsis.sourceforge.io/)). The setup and the `Uninstall DialShift.exe` it writes contain NSIS code: the executable header, the System and nsDialogs plug-ins and the Modern UI, licensed under the **zlib/libpng license**, and the **LZMA decompressor**, licensed under the **Common Public License 1.0** with NSIS's "special exception for LZMA compression module": its authors permit linking to it without subjecting the linked code to the CPL, so DialShift's installer does not fall under the CPL. The module itself stays under the CPL and is unmodified; its source is in `nsis-3.12-src.tar.bz2` at [nsis.sourceforge.io/Download](https://nsis.sourceforge.io/Download). The bzip2 module is not used, nor the Modern UI's own wizard bitmap: the Welcome and Finish image is DialShift's own, drawn from its app icon by `scripts/windows-setup/wizard-image.py` (decision D104). The full text is `NSIS-COPYING.txt`, the `COPYING` file of NSIS 3.12, verbatim; the setup installs it in `licenses\`, and the zip does not carry it.

DialShift's own installer script (`scripts/windows-setup/DialShift.nsi`) is part of this repository, not a third-party component.

## Station catalog data (`app-catalog.json`)

Both packages carry the station catalog behind the Add-station search: `app-catalog.json` next to `DialShift.exe` in the Windows zip, and `DialShift.app/Contents/Resources/app/app-catalog.json` in the macOS bundle. The file is generated by `data/build/build_all.py` in this repository and checked in as `data/output/app-catalog.json`; the curated source files are `data/countries/*.yaml` and `data/collections/*.yaml`, and `data/README.md` describes the pipeline. Its entries are compiled from three sources:

- **radio-browser.info** ([website](https://www.radio-browser.info/)), an open community directory of internet radio stations. It supplies the stream URLs, codec, bitrate, votes and most logo URLs (its station favicons). For stations found only there, it also supplies the name, the city and region (its `state`), the language and the tags that become the `Tags: …` notes, and the type and genre are derived from its tags. Its homepage (retrieved 2026-09-25) gives the data license as public domain: "Everyone is free to use the collected data (station names, tags, links to stream, links to homepages, language, country, state) in their works. I give all the rights I have at the accumulated data to the public domain." No attribution is required; this credit is a courtesy. The radio-browser server software (GPL) is not part of DialShift.
- **Wikipedia**: the English Wikipedia article [List of radio stations in Greece](https://en.wikipedia.org/wiki/List_of_radio_stations_in_Greece), the only list a country file names today (`wiki.url` in `data/countries/greece.yaml`). It supplies the Greek stations' names, frequencies, regions and cities, and its description column becomes the `notes` text of the Greek entries whose curated facts have no notes of their own. That text is by Wikipedia contributors (the article's page history lists them) and is licensed under the [Creative Commons Attribution-ShareAlike 4.0 International license (CC BY-SA 4.0)](https://creativecommons.org/licenses/by-sa/4.0/), without warranties. It was adapted: links and HTML removed from the table cells, `_emphasis_` markers dropped, whitespace trimmed.
- **Curated facts**: the DialShift project's own YAML files in `data/countries/` and `data/collections/`, with local names, types, genres and notes for major stations, and stream and logo URLs pinned to official sources.

**Share-alike.** The `notes` text taken from Wikipedia stays under CC BY-SA 4.0 wherever it is copied, including a station's notes. Anyone who redistributes or adapts that text must credit the Wikipedia contributors and share it under the same license. This applies to that text, not to DialShift's software. The license is linked here rather than copied into `licenses/`, because CC BY-SA 4.0 accepts a link to the license in place of its full text.

**Streams and logos are links.** The catalog stores stream and logo URLs only. The audio and the logo images stay on the stations' and third parties' servers, and neither is part of the packages or of this repository; DialShift fetches them from those servers when it plays a station or shows a logo. Station names, logos and trademarks belong to their owners, and DialShift is not affiliated with the stations, radio-browser.info or Wikipedia.

## Not shipped

The test project `DialShift.Tests` also uses **Avalonia.Headless 12.1.2** (MIT) for its headless UI checks and, when built on Windows, the VLC runtime above for its LibVLC checks. Neither package's test use ships: `DialShift.Tests` is not part of either package. Build-time packages such as `Avalonia.BuildServices` are not shipped either.

## License texts

The texts are in [`licenses/`](licenses/) in the repository. Each package carries the ones that apply to it: `licenses/` in the Windows zip and in the folder the Windows setup installs, and `DialShift.app/Contents/Resources/licenses/` in the macOS bundle.

| File | Covers | Windows | macOS |
|---|---|---|---|
| `Avalonia-LICENSE.txt` | Avalonia | Yes | Yes |
| `SkiaSharp-LICENSE.txt`, `SkiaSharp-THIRD-PARTY-NOTICES.txt` | SkiaSharp and native Skia | Yes | Yes |
| `HarfBuzzSharp-LICENSE.txt`, `HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` | HarfBuzzSharp and native HarfBuzz | Yes | Yes |
| `MicroCom-LICENSE.txt` | MicroCom.Runtime | Yes | Yes |
| `Tmds.DBus-LICENSE.txt` | Tmds.DBus.Protocol | Yes | Yes |
| `NET-LICENSE.txt`, `NET-THIRD-PARTY-NOTICES.txt` | .NET runtime and Microsoft.Extensions/Microsoft.Win32 packages | Yes | Yes |
| `LibVLC-LGPL-2.1.txt` | LibVLCSharp (both); LibVLC runtime (Windows) | Yes | Yes (managed `LibVLCSharp.dll` only) |
| `VLC-GPL-2.0.txt` | GPL-licensed VLC plugins | Yes | No |
| `ANGLE-LICENSE.txt` | ANGLE (`av_libglesv2.dll`) | Yes | No |
| `NSIS-COPYING.txt` | NSIS 3.12 in the Windows setup and its uninstaller | Windows setup only | No |

The CC BY-SA 4.0 license of the catalog's Wikipedia text is linked under [Station catalog data](#station-catalog-data-app-catalogjson), not included in `licenses/`.

No changes to these third-party libraries were made. Station names, broadcasts and programming belong to the respective providers. The included links are for personal listening.
