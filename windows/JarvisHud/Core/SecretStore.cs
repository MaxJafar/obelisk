using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace JarvisHud.Core;

/// <summary>
/// Keeps secrets (device private key, Gateway tokens) on disk encrypted with DPAPI, bound to
/// the current Windows user. Nothing secret is ever written to settings.json or to logs.
/// </summary>
public static class SecretStore
{
    private static string PathFor(string name) => Path.Combine(Settings.Directory, name + ".bin");

    public static byte[]? Read(string name)
    {
        try
        {
            var path = PathFor(name);
            return File.Exists(path) ? Unprotect(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    public static string? ReadString(string name)
    {
        var bytes = Read(name);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public static void Write(string name, byte[] secret)
    {
        Directory.CreateDirectory(Settings.Directory);
        var path = PathFor(name);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Protect(secret));
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteString(string name, string secret) => Write(name, Encoding.UTF8.GetBytes(secret));

    public static void Delete(string name)
    {
        try
        {
            File.Delete(PathFor(name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean up if the file is already gone or locked.
        }
    }

    // ---- DPAPI ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    private static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var blobIn = new DataBlob { Size = input.Length, Data = pin.AddrOfPinnedObject() };
            DataBlob blobOut;
            var ok = protect
                ? CryptProtectData(ref blobIn, "JarvisHud", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out blobOut)
                : CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out blobOut);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var output = new byte[blobOut.Size];
                Marshal.Copy(blobOut.Data, output, 0, blobOut.Size);
                return output;
            }
            finally
            {
                LocalFree(blobOut.Data);
            }
        }
        finally
        {
            pin.Free();
        }
    }
}
