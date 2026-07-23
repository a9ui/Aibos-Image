using System.IO;
using System.Text;

namespace PhotoViewer.Wpf;

internal enum SharedJsonDocumentReadStatus
{
    Success,
    Missing,
    Protected,
}

internal readonly record struct SharedJsonDocumentReadResult(
    SharedJsonDocumentReadStatus Status,
    string? Json,
    string? Error)
{
    public bool Ok => Status is not SharedJsonDocumentReadStatus.Protected;
    public bool Exists => Status is not SharedJsonDocumentReadStatus.Missing;
}

/// <summary>
/// Reads the small durable JSON documents whose bytes are shared with the
/// independent Browser application.  The byte boundary is intentional: a
/// permissive decoder must never normalize protected input before a writer
/// rereads and merges it.
/// </summary>
internal static class SharedJsonDocumentReader
{
    internal const int MaxDocumentBytes = 1_048_576;

    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static SharedJsonDocumentReadResult Read(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxDocumentBytes)
            {
                return Protected(
                    $"Shared JSON exceeds the {MaxDocumentBytes}-byte size limit.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return Protected(
                    "Shared JSON changed while it was being read.");
            }
        }
        catch (FileNotFoundException)
        {
            return new SharedJsonDocumentReadResult(
                SharedJsonDocumentReadStatus.Missing,
                null,
                null);
        }
        catch (DirectoryNotFoundException)
        {
            return new SharedJsonDocumentReadResult(
                SharedJsonDocumentReadStatus.Missing,
                null,
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Protected($"Shared JSON could not be read: {ex.Message}");
        }

        if (HasUtf32Bom(bytes) || HasUtf16Bom(bytes))
            return Protected("Shared JSON must not use UTF-16 or UTF-32 encoding.");

        int offset = HasUtf8Bom(bytes) ? 3 : 0;
        try
        {
            string json = StrictUtf8WithoutBom.GetString(
                bytes,
                offset,
                bytes.Length - offset);
            return new SharedJsonDocumentReadResult(
                SharedJsonDocumentReadStatus.Success,
                json,
                null);
        }
        catch (DecoderFallbackException)
        {
            return Protected("Shared JSON contains invalid UTF-8 bytes.");
        }
    }

    public static bool TryEncodeCanonical(
        string json,
        out byte[] bytes,
        out string? error)
    {
        bytes = [];
        error = null;
        try
        {
            bytes = StrictUtf8WithoutBom.GetBytes(json);
        }
        catch (EncoderFallbackException)
        {
            error = "Shared JSON contains text that cannot be encoded as strict UTF-8.";
            return false;
        }

        if (bytes.Length <= MaxDocumentBytes)
            return true;

        bytes = [];
        error = $"Shared JSON would exceed the {MaxDocumentBytes}-byte size limit.";
        return false;
    }

    private static SharedJsonDocumentReadResult Protected(string error)
        => new(
            SharedJsonDocumentReadStatus.Protected,
            null,
            error);

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF;

    private static bool HasUtf16Bom(byte[] bytes)
        => bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                || (bytes[0] == 0xFE && bytes[1] == 0xFF));

    private static bool HasUtf32Bom(byte[] bytes)
        => bytes.Length >= 4
            && ((bytes[0] == 0xFF
                    && bytes[1] == 0xFE
                    && bytes[2] == 0x00
                    && bytes[3] == 0x00)
                || (bytes[0] == 0x00
                    && bytes[1] == 0x00
                    && bytes[2] == 0xFE
                    && bytes[3] == 0xFF));
}
