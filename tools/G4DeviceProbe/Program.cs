using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

return args switch
{
    ["inspect", var path] => Inspect(path),
    ["unprotect", var path] => Unprotect(path),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: G4DeviceProbe <inspect|unprotect> <device.g4i>");
    return 2;
}

static int Inspect(string path)
{
    try
    {
        var record = DeviceRecord.Read(path);
        Console.WriteLine($"format={record.Magic}");
        Console.WriteLine($"product={record.Product}");
        Console.WriteLine($"release={record.Release}");
        Console.WriteLine($"spki_bytes={record.SubjectPublicKeyInfo.Length}");
        Console.WriteLine($"public_fingerprint={Convert.ToHexString(record.PublicFingerprint).ToLowerInvariant()}");
        Console.WriteLine($"binding={Convert.ToHexString(record.Binding).ToLowerInvariant()}");
        Console.WriteLine($"dpapi_bytes={record.ProtectedPrivateRecord.Length}");
        Console.WriteLine("structure=valid");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"structure=invalid: {exception.Message}");
        return 1;
    }
}

static int Unprotect(string path)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("unprotect requires the Windows user that activated the G4 player");
        return 3;
    }

    var record = DeviceRecord.Read(path);
    byte[]? plaintext = null;

    try
    {
        plaintext = Dpapi.Unprotect(record.ProtectedPrivateRecord);
        Console.WriteLine($"dpapi_layer_1_bytes={plaintext.Length}");

        if (Dpapi.LooksProtected(plaintext))
        {
            var outer = plaintext;
            plaintext = Dpapi.Unprotect(outer);
            CryptographicOperations.ZeroMemory(outer);
            Console.WriteLine($"dpapi_layer_2_bytes={plaintext.Length}");
        }

        var result = PrivateKeyProbe.TryMatch(plaintext, record.SubjectPublicKeyInfo);
        Console.WriteLine($"private_record_format={result.Format}");
        Console.WriteLine($"public_key_match={result.PublicKeyMatch.ToString().ToLowerInvariant()}");
        Console.WriteLine($"rsa_bits={result.RsaBits}");
        return result.PublicKeyMatch ? 0 : 4;
    }
    catch (Win32Exception exception)
    {
        Console.Error.WriteLine($"dpapi=failed win32={exception.NativeErrorCode}: {exception.Message}");
        return 5;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"probe=failed: {exception.Message}");
        return 6;
    }
    finally
    {
        if (plaintext is not null)
            CryptographicOperations.ZeroMemory(plaintext);
    }
}

internal sealed record DeviceRecord(
    string Magic,
    string Product,
    string Release,
    byte[] SubjectPublicKeyInfo,
    byte[] PublicFingerprint,
    byte[] Binding,
    byte[] ProtectedPrivateRecord)
{
    public static DeviceRecord Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 0x239 || Encoding.ASCII.GetString(data, 0, 8) != "G4ID0001")
            throw new InvalidDataException("not a G4ID0001 device record");

        var offset = 12;
        var product = ReadString(data, ref offset);
        var release = ReadString(data, ref offset);

        // ponytail: this is the observed G4ID0001 v1 layout; add version dispatch
        // only if a second on-disk version appears.
        if (offset != 0x45)
            throw new InvalidDataException($"unexpected identity header length 0x{offset:x}");

        const int spkiLengthOffset = 0x4b;
        const int spkiOffset = 0x4f;
        var spkiLength = ReadLength(data, spkiLengthOffset);
        var spkiEnd = checked(spkiOffset + spkiLength);
        var fingerprintEnd = checked(spkiEnd + 32);
        var bindingEnd = checked(fingerprintEnd + 32);
        if (bindingEnd + 4 > data.Length)
            throw new InvalidDataException("truncated identity fields");

        var protectedLength = ReadLength(data, bindingEnd);
        var protectedOffset = bindingEnd + 4;
        if (protectedOffset + protectedLength != data.Length)
            throw new InvalidDataException("protected record does not end at EOF");

        var spki = data[spkiOffset..spkiEnd];
        var fingerprint = data[spkiEnd..fingerprintEnd];
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(spki), fingerprint))
            throw new InvalidDataException("SPKI fingerprint mismatch");

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(spki, out var consumed);
        if (consumed != spki.Length || rsa.KeySize != 3072)
            throw new InvalidDataException("expected one RSA-3072 SPKI");

        var protectedRecord = data[protectedOffset..];
        if (!Dpapi.LooksProtected(protectedRecord))
            throw new InvalidDataException("protected record is not a DPAPI blob");

        return new DeviceRecord(
            "G4ID0001",
            product,
            release,
            spki,
            fingerprint,
            data[fingerprintEnd..bindingEnd],
            protectedRecord);
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;
        var value = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return value;
    }

    private static int ReadLength(byte[] data, int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        if (value > int.MaxValue)
            throw new InvalidDataException("field is too large");
        return (int)value;
    }
}

internal static class Dpapi
{
    private static readonly byte[] ProviderPrefix =
    [
        0x01, 0x00, 0x00, 0x00, 0xd0, 0x8c, 0x9d, 0xdf,
        0x01, 0x15, 0xd1, 0x11, 0x8c, 0x7a, 0x00, 0xc0,
        0x4f, 0xc2, 0x97, 0xeb,
    ];

    public static bool LooksProtected(ReadOnlySpan<byte> data) => data.StartsWith(ProviderPrefix);

    public static byte[] Unprotect(byte[] protectedData)
    {
        var inputPointer = Marshal.AllocHGlobal(protectedData.Length);
        try
        {
            Marshal.Copy(protectedData, 0, inputPointer, protectedData.Length);
            var input = new DataBlob(protectedData.Length, inputPointer);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var plaintext = new byte[output.Length];
                Marshal.Copy(output.Pointer, plaintext, 0, output.Length);
                return plaintext;
            }
            finally
            {
                if (output.Pointer != IntPtr.Zero)
                {
                    Marshal.Copy(new byte[output.Length], 0, output.Pointer, output.Length);
                    LocalFree(output.Pointer);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Pointer;

        public DataBlob(int length, IntPtr pointer)
        {
            Length = length;
            Pointer = pointer;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal readonly record struct PrivateKeyProbeResult(string Format, bool PublicKeyMatch, int RsaBits);

internal static class PrivateKeyProbe
{
    public static PrivateKeyProbeResult TryMatch(ReadOnlySpan<byte> plaintext, byte[] expectedSpki)
    {
        for (var offset = 0; offset < plaintext.Length; offset++)
        {
            if (plaintext[offset] != 0x30)
                continue;

            foreach (var format in new[] { "pkcs8", "pkcs1" })
            {
                using var rsa = RSA.Create();
                try
                {
                    int consumed;
                    if (format == "pkcs8")
                        rsa.ImportPkcs8PrivateKey(plaintext[offset..], out consumed);
                    else
                        rsa.ImportRSAPrivateKey(plaintext[offset..], out consumed);

                    if (consumed == 0)
                        continue;

                    var actualSpki = rsa.ExportSubjectPublicKeyInfo();
                    var matches = CryptographicOperations.FixedTimeEquals(actualSpki, expectedSpki);
                    CryptographicOperations.ZeroMemory(actualSpki);
                    if (matches)
                        return new PrivateKeyProbeResult(format, true, rsa.KeySize);
                }
                catch (CryptographicException)
                {
                    // Keep scanning the small in-memory envelope; no bytes are emitted.
                }
            }
        }

        return new PrivateKeyProbeResult("unrecognized", false, 0);
    }
}
