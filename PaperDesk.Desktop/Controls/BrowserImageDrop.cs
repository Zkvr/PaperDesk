using System.Runtime.InteropServices;
using Ole = System.Runtime.InteropServices.ComTypes;

namespace PaperDesk.Desktop.Controls;

internal static class BrowserImageDrop
{
    internal const int MaxInputBytes = 20 * 1024 * 1024;
    private const string Contents = "FileContents";

    internal static bool HasImageFile(IDataObject data) =>
        data.GetDataPresent("FileGroupDescriptorW", false) || data.GetDataPresent(Contents, false);

    internal static byte[]? Read(IDataObject data)
    {
        if (data is Ole.IDataObject native)
        {
            var format = new Ole.FORMATETC
            {
                cfFormat = unchecked((short)RegisterClipboardFormat(Contents)),
                dwAspect = Ole.DVASPECT.DVASPECT_CONTENT,
                tymed = Ole.TYMED.TYMED_ISTREAM | Ole.TYMED.TYMED_HGLOBAL,
                lindex = 0
            };
            // Chromium offers the dragged image as a virtual file at index zero.
            if (native.QueryGetData(ref format) == 0)
            {
                native.GetData(ref format, out var medium);
                try
                {
                    if (medium.tymed == Ole.TYMED.TYMED_HGLOBAL) return ReadGlobal(medium.unionmember);
                    if (medium.tymed == Ole.TYMED.TYMED_ISTREAM &&
                        Marshal.GetObjectForIUnknown(medium.unionmember) is Ole.IStream stream)
                        return ReadStream(stream);
                }
                finally { ReleaseStgMedium(ref medium); }
            }
        }

        return data.GetData(Contents, false) switch
        {
            byte[] bytes when bytes.Length <= MaxInputBytes => bytes,
            MemoryStream stream when stream.Length <= MaxInputBytes => stream.ToArray(),
            _ => null
        };
    }

    private static byte[]? ReadGlobal(nint handle)
    {
        var size = GlobalSize(handle);
        if (size == 0 || size > MaxInputBytes) return null;
        var address = GlobalLock(handle);
        if (address == 0) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    private static byte[]? ReadStream(Ole.IStream source)
    {
        var count = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            using var destination = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                Marshal.WriteInt32(count, 0);
                source.Read(buffer, buffer.Length, count);
                var length = Marshal.ReadInt32(count);
                if (length == 0) return destination.ToArray();
                if (length < 0 || length > buffer.Length || destination.Length + length > MaxInputBytes) return null;
                destination.Write(buffer, 0, length);
            }
        }
        finally { Marshal.FreeHGlobal(count); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref Ole.STGMEDIUM medium);
}
