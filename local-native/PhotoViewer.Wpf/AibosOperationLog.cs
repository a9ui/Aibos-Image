using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

/// <summary>
/// Small, best-effort operational log for explicit user actions and failures.
/// It never records prompts, source paths, job ids, response bodies, or secrets.
/// File I/O is drained on a background thread so UI actions only enqueue one
/// bounded JSON line.
/// </summary>
internal static partial class AibosOperationLog
{
    private const long MaximumDailyBytes = 4L * 1024 * 1024;
    private const int MaximumPendingLines = 256;
    private const int RetentionDays = 7;
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static int _pendingCount;
    private static int _drainScheduled;
    private static int _cleanupAttempted;

    public static bool Enabled { get; set; } = true;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aibos Image",
        "Logs");

    public static void Write(
        string operation,
        string outcome,
        long elapsedMilliseconds = 0,
        int? statusCode = null,
        string? errorCode = null,
        string? mode = null,
        int? durationSeconds = null,
        double? inferenceMilliseconds = null,
        int? itemCount = null)
    {
        if (!Enabled)
            return;
        try
        {
            var entry = new OperationLogEntry(
                TimestampUtc: DateTimeOffset.UtcNow,
                ProcessId: Environment.ProcessId,
                Operation: SafeToken(operation, "unknown_operation"),
                Outcome: SafeToken(outcome, "unknown"),
                ElapsedMilliseconds: Math.Max(0, elapsedMilliseconds),
                StatusCode: statusCode is >= 0 and <= 999 ? statusCode : null,
                ErrorCode: SafeOptionalToken(errorCode),
                Mode: SafeOptionalToken(mode),
                DurationSeconds: durationSeconds is >= 0 and <= 86_400
                    ? durationSeconds
                    : null,
                InferenceMilliseconds:
                    inferenceMilliseconds is double inference
                        && double.IsFinite(inference)
                        && inference >= 0
                        && inference <= 600_000
                            ? Math.Round(inference, 1)
                            : null,
                ItemCount: itemCount is >= 0 and <= 1_000_000
                    ? itemCount
                    : null);
            string line = JsonSerializer.Serialize(entry);
            if (Utf8NoBom.GetByteCount(line) > 2_048)
                return;

            Pending.Enqueue(line);
            int count = Interlocked.Increment(ref _pendingCount);
            while (count > MaximumPendingLines && Pending.TryDequeue(out _))
            {
                count = Interlocked.Decrement(ref _pendingCount);
            }
            ScheduleDrain();
        }
        catch
        {
            // Diagnostics must never affect the product action.
        }
    }

    private static void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref _drainScheduled, 1, 0) != 0)
            return;
        _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            CleanupExpiredLogsOnce();
            string path = Path.Combine(
                DirectoryPath,
                $"operations-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            var batch = new StringBuilder(8 * 1024);
            while (Pending.TryDequeue(out string? line))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.AppendLine(line);
                if (batch.Length >= 8 * 1024)
                {
                    AppendBounded(path, batch);
                    batch.Clear();
                }
            }
            if (batch.Length > 0)
                AppendBounded(path, batch);
        }
        catch
        {
            // Best effort only. A full or unavailable log directory must not
            // block image browsing or AI actions.
        }
        finally
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            if (!Pending.IsEmpty)
                ScheduleDrain();
        }
    }

    private static void AppendBounded(string path, StringBuilder batch)
    {
        long existingBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        int batchBytes = Utf8NoBom.GetByteCount(batch.ToString());
        if (existingBytes + batchBytes > MaximumDailyBytes)
            return;
        File.AppendAllText(path, batch.ToString(), Utf8NoBom);
    }

    private static void CleanupExpiredLogsOnce()
    {
        if (Interlocked.Exchange(ref _cleanupAttempted, 1) != 0)
            return;
        DateTime cutoffUtc = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (string candidate in Directory.EnumerateFiles(
                     DirectoryPath,
                     "operations-????-??-??.jsonl",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(candidate) < cutoffUtc)
                    File.Delete(candidate);
            }
            catch
            {
            }
        }
    }

    private static string SafeToken(string? value, string fallback)
    {
        string candidate = value?.Trim() ?? "";
        return candidate.Length is >= 1 and <= 80
            && SafeTokenPattern().IsMatch(candidate)
                ? candidate
                : fallback;
    }

    private static string? SafeOptionalToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : SafeToken(value, "invalid_token");

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTokenPattern();

    private sealed record OperationLogEntry(
        DateTimeOffset TimestampUtc,
        int ProcessId,
        string Operation,
        string Outcome,
        long ElapsedMilliseconds,
        int? StatusCode,
        string? ErrorCode,
        string? Mode,
        int? DurationSeconds,
        double? InferenceMilliseconds,
        int? ItemCount);
}
