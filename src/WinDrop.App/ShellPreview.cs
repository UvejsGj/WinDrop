using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinDrop.App;

/// <summary>
/// An image to show for a file. <see cref="IsOpaque"/> decides how it is framed: an
/// opaque picture is the file's contents and gets a card with rounded corners, while
/// anything with transparency is artwork that already has its own shape — a type icon, a
/// folder — and stands on the glass unframed.
/// </summary>
public sealed record FilePreview(BitmapSource Image, bool IsOpaque);

/// <summary>
/// Previews come from the Windows shell rather than from decoding files ourselves.
///
/// "What would a preview of a non-image even be?" already has an answer on every machine
/// this runs on: Explorer's thumbnail handlers. Whatever is installed — the photo handler,
/// video frame extraction, Office's first page, a PDF reader's — registers per file type,
/// and <c>IShellItemImageFactory</c> asks all of them through one call. For a type nothing
/// can render it returns the type's icon instead. That is how Finder and the iOS share
/// sheet behave too, and here it comes without WinDrop knowing about a single format.
/// </summary>
public static class ShellPreview
{
    /// <summary>Contents if the shell can picture them, otherwise the type icon.</summary>
    public static Task<FilePreview?> ForPathAsync(string path, int pixels) => RunOnSta(() =>
        GetImage(path, pixels, ImageFactoryFlags.ThumbnailOnly)
        ?? GetImage(path, pixels, ImageFactoryFlags.IconOnly));

    /// <summary>A picture of the contents, or null. Never falls back to an icon.</summary>
    internal static FilePreview? ContentThumbnail(string path, int pixels) =>
        GetImage(path, pixels, ImageFactoryFlags.ThumbnailOnly);

    /// <summary>
    /// The icon for a file that does not exist on this machine — the receiving side, where
    /// all we have is a name from the /Ask body.
    /// </summary>
    public static Task<FilePreview?> ForTypeAsync(string fileName, bool isDirectory) =>
        RunOnSta(() => TypeIcon(fileName, isDirectory));

    /// <summary>
    /// Shell work happens on a thread of its own, in a single-threaded apartment.
    ///
    /// Thumbnail handlers are in-process COM servers written by whoever registered them,
    /// and plenty are apartment-threaded; called from a thread-pool thread they fail or try
    /// to marshal through an apartment that is not pumping. A dedicated thread also keeps a
    /// slow handler — the video one pulling a frame off a network share — away from the UI.
    /// </summary>
    internal static Task<T> RunOnSta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                // Imaging objects quietly create a Dispatcher for the thread they are
                // made on. Everything handed back is frozen and no longer tied to it.
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "WinDrop shell preview",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    private static FilePreview? GetImage(string path, int pixels, ImageFactoryFlags flags)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID,
                out IShellItemImageFactory? factory) < 0 || factory is null)
        {
            return null;
        }

        IntPtr hbitmap = IntPtr.Zero;

        try
        {
            var size = new NativeSize { Width = pixels, Height = pixels };

            if (factory.GetImage(size, flags, out hbitmap) < 0 || hbitmap == IntPtr.Zero)
                return null;

            return FromHBitmap(hbitmap);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the shell's bitmap out with its alpha intact.
    ///
    /// The obvious <c>Imaging.CreateBitmapSourceFromHBitmap</c> drops the alpha channel,
    /// which puts a black square behind every icon. So the pixels are read directly and
    /// the alpha dealt with by hand.
    /// </summary>
    private static FilePreview? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap bitmap) == 0)
            return null;

        int width = bitmap.Width;
        int height = Math.Abs(bitmap.Height);
        if (width <= 0 || height <= 0) return null;

        // Negative height asks for rows top-down, which is the order WPF wants them in,
        // whatever orientation the shell happened to allocate the bitmap with.
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };

        var pixels = new byte[width * height * 4];
        IntPtr screen = GetDC(IntPtr.Zero);

        try
        {
            if (GetDIBits(screen, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }

        bool anyAlpha = false;
        bool anyTransparency = false;

        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) anyAlpha = true;
            if (pixels[i] != 0xFF) anyTransparency = true;
        }

        // Handlers that produce plain RGB leave the alpha byte at zero rather than 255, so
        // taken literally a photo comes back entirely invisible. A thumbnail that is 100%
        // transparent is never a real result; it means there is no alpha channel at all.
        if (!anyAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
            anyTransparency = false;
        }

        // Straight alpha, not premultiplied — measured, not assumed. Premultiplied data can
        // never have a colour channel above its own alpha, and explorer.exe's icon came
        // back with 78 pixels that do, by as much as 207. Labelled Pbgra32, those edge
        // pixels would render as a bright halo around every icon.
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();

        return new FilePreview(source, IsOpaque: !anyTransparency);
    }

    private static FilePreview? TypeIcon(string fileName, bool isDirectory)
    {
        // The name comes from an unauthenticated peer. Only its extension is used, only if
        // it is plain, and only as a lookup key: USEFILEATTRIBUTES means the shell answers
        // from the registry and never goes looking for a file by that name.
        string extension = Path.GetExtension(fileName);

        if (extension.Length is < 2 or > 16 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
            extension = "";

        var info = new ShFileInfo();
        uint attributes = isDirectory ? FileAttributeDirectory : FileAttributeNormal;

        if (SHGetFileInfo("preview" + extension, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiSysIconIndex | ShgfiUseFileAttributes) == IntPtr.Zero)
        {
            return null;
        }

        if (SHGetImageList(ShilJumbo, typeof(IImageList).GUID, out IImageList? list) < 0 || list is null)
            return null;

        IntPtr hicon = IntPtr.Zero;

        try
        {
            if (list.GetIcon(info.IconIndex, IldTransparent, out hicon) < 0 || hicon == IntPtr.Zero)
                return null;

            BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();

            return new FilePreview(icon, IsOpaque: false);
        }
        finally
        {
            if (hicon != IntPtr.Zero) DestroyIcon(hicon);
            Marshal.ReleaseComObject(list);
        }
    }

    // ---- interop -----------------------------------------------------------

    [Flags]
    private enum ImageFactoryFlags
    {
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ImageFactoryFlags flags, out IntPtr hbitmap);
    }

    // Declared only as far as GetIcon; COM dispatches by vtable slot, so the methods
    // before it have to be present and in order even though nothing calls them.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
        [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int result);
        [PreserveSig] int SetOverlayImage(int image, int overlay);
        [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
        [PreserveSig] int AddMasked(IntPtr image, int maskColour, out int index);
        [PreserveSig] int Draw(IntPtr parameters);
        [PreserveSig] int Remove(int index);
        [PreserveSig] int GetIcon(int index, int flags, out IntPtr icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    private const uint ShgfiSysIconIndex = 0x4000;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const int ShilJumbo = 4;
    private const int IldTransparent = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int list, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? imageList);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] bits, ref BitmapInfoHeader header, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
