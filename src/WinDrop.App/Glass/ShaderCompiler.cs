using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Effects;

namespace WinDrop.App.Glass;

/// <summary>
/// Compiles HLSL at startup, with the shader compiler Windows already ships.
///
/// WPF only accepts compiled pixel-shader bytecode. The usual route is fxc from the Windows
/// SDK at build time with the binary checked in, which leaves the repo holding a blob that
/// nobody can read and that can silently drift from its source. d3dcompiler_47.dll is part
/// of Windows 10 and 11 themselves, so the .hlsl can be the only artefact: whoever clones
/// this runs exactly the shader they read.
/// </summary>
internal static class ShaderCompiler
{
    private const uint OptimizationLevel3 = 1 << 15;

    public static PixelShader? Compile(string resourceName, out string? error)
    {
        error = null;

        using Stream? stream = typeof(ShaderCompiler).Assembly.GetManifestResourceStream(resourceName);

        if (stream is null)
        {
            error = $"shader resource '{resourceName}' is missing";
            return null;
        }

        byte[] source;
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            source = buffer.ToArray();
        }

        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;

        try
        {
            // ps_3_0 rather than 2_0: the lens needs more arithmetic than 2_0's budget allows.
            // The cost is that WPF's software renderer cannot run it, which the caller checks
            // for separately.
            int hr = D3DCompile(source, (IntPtr)source.Length, resourceName, IntPtr.Zero, IntPtr.Zero,
                "main", "ps_3_0", OptimizationLevel3, 0, out code, out errors);

            if (hr < 0 || code == IntPtr.Zero)
            {
                error = errors == IntPtr.Zero ? $"HRESULT 0x{hr:X8}" : Encoding.ASCII.GetString(ReadBlob(errors)).TrimEnd('\0', '\r', '\n');
                return null;
            }

            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(ReadBlob(code)));
            shader.Freeze();

            return shader;
        }
        catch (Exception ex)
        {
            // Glass is decoration. Whatever goes wrong here — a missing DLL, a driver that
            // rejects the bytecode — the window must still open, with plain frosted panes.
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }

    /// <summary>
    /// Copies an ID3DBlob's bytes out by calling its vtable directly.
    ///
    /// Declaring the interface for COM interop does not work: the runtime's marshaller
    /// QueryInterfaces for ID3D10Blob's IID, and the blob that d3dcompiler_47 returns
    /// answers E_NOINTERFACE to it — measured, it took the whole window down on first
    /// launch. The layout itself is fixed by the ABI: after IUnknown's three methods come
    /// GetBufferPointer and GetBufferSize, both taking only the object pointer.
    /// </summary>
    private static byte[] ReadBlob(IntPtr blob)
    {
        IntPtr vtable = Marshal.ReadIntPtr(blob);

        var getPointer = Marshal.GetDelegateForFunctionPointer<BlobMethod>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
        var getSize = Marshal.GetDelegateForFunctionPointer<BlobMethod>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));

        var bytes = new byte[(int)getSize(blob)];
        Marshal.Copy(getPointer(blob), bytes, 0, bytes.Length);
        return bytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr BlobMethod(IntPtr self);

    [DllImport("d3dcompiler_47.dll")]
    private static extern int D3DCompile(
        byte[] source, IntPtr sourceSize,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines, IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags, uint effectFlags,
        out IntPtr code,
        out IntPtr errors);
}
