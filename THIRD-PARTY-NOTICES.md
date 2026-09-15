# Third-party components

DialShift uses unmodified, dynamically linked dependencies:

- **LibVLCSharp 3.10.1** — LGPL-2.1-or-later. [Project and source](https://code.videolan.org/videolan/LibVLCSharp), [NuGet](https://www.nuget.org/packages/LibVLCSharp/3.10.1).
- **Avalonia 11.3.22** (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`) — MIT. The macOS interface; renders with SkiaSharp and HarfBuzzSharp (MIT). [Website](https://avaloniaui.net/), [source](https://github.com/AvaloniaUI/Avalonia), [license](https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md).
- **VideoLAN.LibVLC.Windows 3.0.23.1** — VLC/LibVLC native runtime and plugins for Windows. LibVLC is LGPL-2.1-or-later; bundled plugins and their dependencies have individual licenses, including GPL components. [Packaging/source instructions](https://code.videolan.org/videolan/libvlc-nuget), [VLC source](https://code.videolan.org/videolan/vlc/-/tree/3.0.23), [VideoLAN legal information](https://www.videolan.org/legal.html).
- **VideoLAN.LibVLC.Mac 3.1.3.1** — VLC/LibVLC native runtime for macOS (x86_64). Same LibVLC licensing as the Windows package. [Packaging/source instructions](https://code.videolan.org/videolan/libvlc-nuget).
- **.NET 10** — Microsoft and contributors, MIT and component notices. [Runtime](https://github.com/dotnet/runtime). The Windows app additionally uses WPF and Windows Forms ([WPF](https://github.com/dotnet/wpf), [Windows Forms](https://github.com/dotnet/winforms)); self-contained releases carry runtime license/notice files.

Native VLC libraries remain separate — `libvlc/win-x64` on Windows and the `VideoLAN.LibVLC.Mac` package's bundled `libvlc.dylib` on macOS — so compatible replacements can be supplied. No changes to these third-party libraries were made. Station names, broadcasts and programming belong to the respective providers. The included links are for personal listening.
