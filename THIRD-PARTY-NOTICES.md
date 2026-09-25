# Third-party components

DialShift is released as two self-contained packages built from the single `DialShift.App` project. Each package contains only the components listed for it below. All third-party components are unmodified and dynamically linked. There is no Intel Mac package.

| Package | Artifact | Playback |
|---|---|---|
| Windows (`win-x64`) | `DialShift-win-x64.zip` | LibVLC (bundled) |
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

## License texts

The texts are in [`licenses/`](licenses/) in the repository. Each package carries the ones that apply to it: `licenses/` in the Windows zip, and `DialShift.app/Contents/Resources/licenses/` in the macOS bundle.

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

No changes to these third-party libraries were made. Station names, broadcasts and programming belong to the respective providers. The included links are for personal listening.
