// AVFoundation / CoreMedia / Foundation handles, constants and selectors used by MacAvPlayerPlaybackEngine.
// Frameworks are loaded once and kept for the process lifetime (NativeLibrary handles are never freed: AVFoundation
// cannot be safely unloaded while any of its objects or dispatch work may still exist).
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DialShift.App.Interop;

/// <summary>CoreMedia <c>CMTime</c> (24 bytes, natural alignment; identical layout on arm64 and x86_64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public long Value;
    public int Timescale;
    public uint Flags;
    public long Epoch;

    /// <summary><c>kCMTimeFlags_Valid</c>.</summary>
    public readonly bool IsValid => (Flags & 1) != 0;
}

/// <summary>AVPlayer enums as returned by the NSInteger getters.</summary>
internal static class AVStatus
{
    /// <summary><c>AVPlayerStatus</c> / <c>AVPlayerItemStatus</c>.</summary>
    public const nint Unknown = 0, ReadyToPlay = 1, Failed = 2;

    /// <summary><c>AVPlayerTimeControlStatus</c>.</summary>
    public const nint Paused = 0, WaitingToPlay = 1, Playing = 2;
}

/// <summary>Loaded frameworks, exported constants and cached selectors/classes.</summary>
[SupportedOSPlatform("macos")]
internal static partial class AVFoundation
{
    private const string CoreMediaPath = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";

    /// <summary><c>Float64 CMTimeGetSeconds(CMTime)</c>: the struct is passed by value (arm64: indirectly via a pointer in x0, handled by the stub).</summary>
    [LibraryImport(CoreMediaPath, EntryPoint = "CMTimeGetSeconds")]
    internal static partial double CMTimeGetSeconds(CMTime time);

    private static readonly nint FoundationLib = NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");
    private static readonly nint AVFoundationLib = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
    private static readonly nint CoreMediaLib = NativeLibrary.Load(CoreMediaPath);

    // Classes (resolved after the frameworks above are loaded; static field initializers run in textual order).
    internal static readonly nint NSURLClass = ObjCRuntime.GetClass("NSURL");
    internal static readonly nint AVURLAssetClass = ObjCRuntime.GetClass("AVURLAsset");
    internal static readonly nint AVPlayerItemClass = ObjCRuntime.GetClass("AVPlayerItem");
    internal static readonly nint AVPlayerClass = ObjCRuntime.GetClass("AVPlayer");

    // Notification names (NSString *const, +0 for the process lifetime) and userInfo keys.
    internal static readonly nint DidPlayToEndTimeNotification = ObjCRuntime.ReadPointerConstant(AVFoundationLib, "AVPlayerItemDidPlayToEndTimeNotification");
    internal static readonly nint FailedToPlayToEndTimeNotification = ObjCRuntime.ReadPointerConstant(AVFoundationLib, "AVPlayerItemFailedToPlayToEndTimeNotification");
    internal static readonly nint PlaybackStalledNotification = ObjCRuntime.ReadPointerConstant(AVFoundationLib, "AVPlayerItemPlaybackStalledNotification");
    internal static readonly nint FailedToPlayToEndTimeErrorKey = ObjCRuntime.ReadPointerConstant(AVFoundationLib, "AVPlayerItemFailedToPlayToEndTimeErrorKey");
    internal static readonly nint UnderlyingErrorKey = ObjCRuntime.ReadPointerConstant(FoundationLib, "NSUnderlyingErrorKey");

    /// <summary>Selectors, each used with the <see cref="ObjCRuntime"/> entry point matching its prototype (noted per line).</summary>
    internal static class Sel
    {
        public static readonly nint InitWithString = ObjCRuntime.Selector("initWithString:");            // id (NSString*), nil on failure
        public static readonly nint InitWithURLOptions = ObjCRuntime.Selector("initWithURL:options:");   // id (NSURL*, NSDictionary*)
        public static readonly nint InitWithAsset = ObjCRuntime.Selector("initWithAsset:");              // id (AVAsset*)
        public static readonly nint ReplaceCurrentItem = ObjCRuntime.Selector("replaceCurrentItemWithPlayerItem:"); // void (AVPlayerItem*)
        public static readonly nint Play = ObjCRuntime.Selector("play");                                  // void
        public static readonly nint Pause = ObjCRuntime.Selector("pause");                                // void
        public static readonly nint SetVolume = ObjCRuntime.Selector("setVolume:");                       // void (float)
        public static readonly nint Volume = ObjCRuntime.Selector("volume");                              // float
        public static readonly nint SetMuted = ObjCRuntime.Selector("setMuted:");                         // void (BOOL)
        public static readonly nint TimeControlStatus = ObjCRuntime.Selector("timeControlStatus");        // NSInteger
        public static readonly nint Status = ObjCRuntime.Selector("status");                              // NSInteger
        public static readonly nint CurrentItem = ObjCRuntime.Selector("currentItem");                    // id (+0)
        public static readonly nint CurrentTime = ObjCRuntime.Selector("currentTime");                    // CMTime
        public static readonly nint Error = ObjCRuntime.Selector("error");                                // NSError* (+0)
        public static readonly nint ErrorLog = ObjCRuntime.Selector("errorLog");                          // AVPlayerItemErrorLog* (+0)
        public static readonly nint Events = ObjCRuntime.Selector("events");                              // NSArray* (+0)
        public static readonly nint LastObject = ObjCRuntime.Selector("lastObject");                      // id (+0)
        public static readonly nint ErrorStatusCode = ObjCRuntime.Selector("errorStatusCode");            // NSInteger
        public static readonly nint Domain = ObjCRuntime.Selector("domain");                              // NSString* (+0)
        public static readonly nint Code = ObjCRuntime.Selector("code");                                  // NSInteger
        public static readonly nint LocalizedDescription = ObjCRuntime.Selector("localizedDescription");  // NSString* (+0)
        public static readonly nint UserInfo = ObjCRuntime.Selector("userInfo");                          // NSDictionary* (+0)
        public static readonly nint ObjectForKey = ObjCRuntime.Selector("objectForKey:");                 // id (id), +0
        public static readonly nint Name = ObjCRuntime.Selector("name");                                  // NSString* (+0)
        public static readonly nint Object = ObjCRuntime.Selector("object");                              // id (+0)
    }

    /// <summary>Forces the static constructor so framework/class/symbol failures surface at a deliberate point.</summary>
    internal static void EnsureLoaded() => GC.KeepAlive(CoreMediaLib);
}
