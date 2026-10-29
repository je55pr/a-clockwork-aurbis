// Optional in-memory decoder for ESO compression 4/8.
// Accepts a user-provided proprietary Oodle DLL, or a locally compiled GPL ooz decoder.
// Neither native DLL is included in the distributable project.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EsoTest;

public sealed unsafe class OodleDecoder : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NativeOodle(
        byte* input, nint inputLength, byte* output, nint outputLength,
        int fuzzSafe, int checkCrc, int verbosity, nint decBufBase, nint decBufSize,
        nint callback, nint callbackUser, nint decoderMemory, nint decoderMemorySize, int threadPhase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeOoz(byte* input, nuint inputLength, byte* output, nuint outputLength);

    private readonly nint _handle;
    private readonly NativeOodle? _oodle;
    private readonly NativeOoz? _ooz;
    public string LibraryPath { get; }

    private OodleDecoder(string dllPath)
    {
        LibraryPath = Path.GetFullPath(dllPath);
        _handle = NativeLibrary.Load(LibraryPath);
        try
        {
            if (NativeLibrary.TryGetExport(_handle, "OodleLZ_Decompress", out nint ptr))
                _oodle = Marshal.GetDelegateForFunctionPointer<NativeOodle>(ptr);
            else if (NativeLibrary.TryGetExport(_handle, "ooz_decompress", out ptr))
                _ooz = Marshal.GetDelegateForFunctionPointer<NativeOoz>(ptr);
            else
                throw new EntryPointNotFoundException("Neither OodleLZ_Decompress nor ooz_decompress is exported");
        }
        catch
        {
            NativeLibrary.Free(_handle);
            throw;
        }
    }

    public static OodleDecoder? FromEnvironment(string? locallyBuiltOoz = null)
    {
        string? path = Environment.GetEnvironmentVariable("ESO_OODLE_DLL");
        if (string.IsNullOrWhiteSpace(path))
            path = Environment.GetEnvironmentVariable("ESO_OOZ_DLL");
        if (string.IsNullOrWhiteSpace(path))
            path = locallyBuiltOoz;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        return new OodleDecoder(path);
    }

    public byte[] Decompress(byte[] data, int expectedLength)
    {
        if (expectedLength <= 0 || expectedLength > 512_000_000 || data.Length == 0)
            throw new InvalidDataException("Invalid decompression sizes");
        var result = new byte[expectedLength];
        fixed (byte* input = data)
        fixed (byte* output = result)
        {
            nint written = _oodle != null
                ? _oodle(input, data.Length, output, expectedLength, 1, 0, 0, 0, 0, 0, 0, 0, 0, 3)
                : _ooz!(input, (nuint)data.Length, output, (nuint)expectedLength);
            if (written != expectedLength)
                throw new InvalidDataException($"Decoder returned {written} bytes, expected {expectedLength}");
        }
        return result;
    }

    public void Dispose() => NativeLibrary.Free(_handle);
}
