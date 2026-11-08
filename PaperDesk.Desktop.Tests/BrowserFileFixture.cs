using System;
using System.Runtime.InteropServices;
using System.Windows;
using Ole = System.Runtime.InteropServices.ComTypes;

// Supplies the same indexed IStream as Chromium, with no managed fallback.
internal sealed class BrowserFileFixture(byte[] bytes) : IDataObject, Ole.IDataObject
{
    private static readonly short FileContents = unchecked((short)RegisterClipboardFormat("FileContents"));
    public bool ReadNativeStream { get; private set; }

    public bool GetDataPresent(string format, bool autoConvert) => format == "FileGroupDescriptorW";
    public bool GetDataPresent(string format) => GetDataPresent(format, false);
    public bool GetDataPresent(Type format) => false;
    public string[] GetFormats(bool autoConvert) => ["FileGroupDescriptorW", "FileContents"];
    public string[] GetFormats() => GetFormats(false);
    public object? GetData(string format, bool autoConvert) => null;
    public object? GetData(string format) => null;
    public object? GetData(Type format) => null;
    public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    public void SetData(string format, object data) => throw new NotSupportedException();
    public void SetData(Type format, object data) => throw new NotSupportedException();
    public void SetData(object data) => throw new NotSupportedException();

    public int QueryGetData(ref Ole.FORMATETC format) =>
        format.cfFormat == FileContents && format.lindex == 0 && (format.tymed & Ole.TYMED.TYMED_ISTREAM) != 0 ? 0 : unchecked((int)0x80040064);

    public void GetData(ref Ole.FORMATETC format, out Ole.STGMEDIUM medium)
    {
        Marshal.ThrowExceptionForHR(QueryGetData(ref format));
        var stream = SHCreateMemStream(bytes, (uint)bytes.Length);
        if (stream == 0) throw new OutOfMemoryException();
        ReadNativeStream = true;
        medium = new Ole.STGMEDIUM { tymed = Ole.TYMED.TYMED_ISTREAM, unionmember = stream };
    }

    public void GetDataHere(ref Ole.FORMATETC format, ref Ole.STGMEDIUM medium) => throw new NotSupportedException();
    public int GetCanonicalFormatEtc(ref Ole.FORMATETC input, out Ole.FORMATETC output) { output = input; return 0x00040130; }
    public void SetData(ref Ole.FORMATETC format, ref Ole.STGMEDIUM medium, bool release) => throw new NotSupportedException();
    public Ole.IEnumFORMATETC EnumFormatEtc(Ole.DATADIR direction) => throw new NotSupportedException();
    public int DAdvise(ref Ole.FORMATETC format, Ole.ADVF flags, Ole.IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) => throw new NotSupportedException();
    public int EnumDAdvise(out Ole.IEnumSTATDATA? enumerator) { enumerator = null; return unchecked((int)0x80040003); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte[] bytes, uint length);
}
