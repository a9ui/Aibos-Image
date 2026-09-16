using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoViewer.Wpf;

public sealed record VideoLoraSelection(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("strength")] double Strength)
{
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    public const string Workflow = "minimax-h3-comfy-lora-v1";

    public static bool TryReadList(JsonElement value, out VideoLoraSelection[]? result)
    {
        result = null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 8) return false;
        var list = new List<VideoLoraSelection>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!TryRead(item, out VideoLoraSelection? entry)) return false;
            list.Add(entry!);
        }
        if (list.Sum(x => x.Bytes) > MaximumBytes || list.Select(x => x.Sha256).Distinct(StringComparer.Ordinal).Count() != list.Count) return false;
        result = list.ToArray(); return true;
    }

    public static bool TryRead(JsonElement value, out VideoLoraSelection? result)
    {
        result = null;
        try
        {
            string[] keys = ["schemaVersion", "directory", "fileName", "sha256", "bytes", "strength"];
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length
                || keys.Any(k => value.EnumerateObject().Count(p => p.NameEquals(k)) != 1)) return false;
            var selection = JsonSerializer.Deserialize<VideoLoraSelection>(value);
            if (selection is null || selection.SchemaVersion != 1 || !selection.ValidNames()
                || selection.Sha256 is null || selection.Sha256.Length != 64
                || selection.Sha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                || selection.Bytes is < 10 or > MaximumBytes || !double.IsFinite(selection.Strength)
                || selection.Strength == 0 || Math.Abs(selection.Strength) > 2) return false;
            result = selection;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException) { return false; }
    }

    internal bool ValidNames()
    {
        if (string.IsNullOrEmpty(Directory) || Directory.Length > 4096 || Directory.Length < 3
            || !char.IsAsciiLetter(Directory[0]) || Directory[1] != ':' || Directory[2] is not ('\\' or '/')
            || Directory.Any(char.IsControl) || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory)),
                Path.TrimEndingDirectorySeparator(Directory.Replace('/', '\\')), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(FileName) || FileName.Length > 240
            || FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || FileName.EndsWith(' ') || FileName.EndsWith('.')
            || !FileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return false;
        string stem = FileName.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL")
            && !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    internal static void RequireNormalPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("LoRAの場所にリンクやジャンクションは使えません。");
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }

    internal async Task<VideoLoraSelection> CaptureAsync(CancellationToken token)
    {
        if (!ValidNames() || !double.IsFinite(Strength) || Strength == 0 || Math.Abs(Strength) > 2)
            throw new InvalidDataException("LoRAの選択か強度を確認してください。");
        string file = Path.Combine(Directory, FileName);
        RequireNormalPath(file);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 10 or > MaximumBytes) throw new InvalidDataException("LoRAは4GiB以下のsafetensorsファイルを選んでください。");
        byte[] prefix = new byte[8];
        await stream.ReadExactlyAsync(prefix, token);
        ulong length = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (length is < 2 or > 4 * 1024 * 1024 || length + 8 >= (ulong)stream.Length)
            throw new InvalidDataException("LoRAのsafetensorsヘッダーを読み取れません。");
        stream.Position = 0;
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        return this with { Sha256 = hash, Bytes = stream.Length };
    }
}
