using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

/// <summary>
/// Small, best-effort operational log for explicit user actions and failures.
/// It never records prompts, source paths, job ids, response bodies, or secrets.
/// Process lifecycle entries may include only a numeric related process id and
/// exit code so an unexpected local companion stop can be diagnosed.
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
        int? itemCount = null,
        int? relatedProcessId = null,
        int? exitCode = null)
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
                    : null,
                RelatedProcessId: relatedProcessId is > 0
                    ? relatedProcessId
                    : null,
                ExitCode: exitCode);
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
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (!TryPrepareTrustedLogDirectory(localAppData, out string trustedDirectory))
                return;

            DateTime utcNow = DateTime.UtcNow;
            CleanupExpiredLogsOnce(trustedDirectory, utcNow);
            var batch = new StringBuilder(8 * 1024);
            while (Pending.TryDequeue(out string? line))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.AppendLine(line);
                if (batch.Length >= 8 * 1024)
                {
                    AppendBounded(trustedDirectory, utcNow, batch);
                    batch.Clear();
                }
            }
            if (batch.Length > 0)
                AppendBounded(trustedDirectory, utcNow, batch);
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

    internal static bool TryWriteBatchForSecuritySmoke(
        string localAppDataRoot,
        DateTime utcNow,
        string line)
    {
        if (!TryPrepareTrustedLogDirectory(
                localAppDataRoot,
                out string trustedDirectory))
        {
            return false;
        }

        CleanupExpiredLogsOnce(trustedDirectory, utcNow);
        var batch = new StringBuilder(line.Length + Environment.NewLine.Length);
        batch.AppendLine(line);
        return AppendBounded(trustedDirectory, utcNow, batch);
    }

    internal static string CompanionLifecycleLineForSecuritySmoke()
        => JsonSerializer.Serialize(new OperationLogEntry(
            TimestampUtc: DateTimeOffset.UnixEpoch,
            ProcessId: 1234,
            Operation: "companion.process",
            Outcome: "unexpected_exit",
            ElapsedMilliseconds: 15_000,
            StatusCode: null,
            ErrorCode: "terminated_or_aborted",
            Mode: "owned",
            DurationSeconds: null,
            InferenceMilliseconds: null,
            ItemCount: null,
            RelatedProcessId: 4321,
            ExitCode: -1));

    private static bool TryPrepareTrustedLogDirectory(
        string localAppDataRoot,
        out string trustedDirectory)
    {
        trustedDirectory = "";
        try
        {
            string expectedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(localAppDataRoot));
            if (!Path.IsPathFullyQualified(expectedRoot)
                || !WindowsPathIdentity.TryResolveExistingDirectory(
                    expectedRoot,
                    out string canonicalRoot)
                || !string.Equals(
                    expectedRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string current = canonicalRoot;
            foreach (string fixedName in new[] { "Aibos Image", "Logs" })
            {
                string candidate = Path.GetFullPath(Path.Combine(current, fixedName));
                if (!string.Equals(
                        Path.GetDirectoryName(candidate),
                        current,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // current was resolved from an open directory handle and fixedName
                // is application-owned. The created child is re-opened and checked
                // before it is trusted by any write or delete operation.
                // codeql[cs/path-injection]
                Directory.CreateDirectory(candidate);
                FileAttributes attributes = File.GetAttributes(candidate);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !WindowsPathIdentity.TryResolveExistingDirectory(
                        candidate,
                        out string canonicalCandidate)
                    || !string.Equals(
                        candidate,
                        canonicalCandidate,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                current = canonicalCandidate;
            }

            trustedDirectory = current;
            return true;
        }
        catch
        {
            trustedDirectory = "";
            return false;
        }
    }

    private static bool IsTrustedLogDirectory(string trustedDirectory)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(trustedDirectory);
            return attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint)
                && WindowsPathIdentity.TryResolveExistingDirectory(
                    trustedDirectory,
                    out string canonicalDirectory)
                && string.Equals(
                    trustedDirectory,
                    canonicalDirectory,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool AppendBounded(
        string trustedDirectory,
        DateTime utcNow,
        StringBuilder batch)
    {
        if (!IsTrustedLogDirectory(trustedDirectory))
            return false;

        string expectedPath = Path.GetFullPath(Path.Combine(
            trustedDirectory,
            $"operations-{utcNow:yyyy-MM-dd}.jsonl"));
        if (!string.Equals(
                Path.GetDirectoryName(expectedPath),
                trustedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] payload = Utf8NoBom.GetBytes(batch.ToString());
        if (payload.LongLength > MaximumDailyBytes)
            return false;

        // trustedDirectory is a direct, handle-resolved application child. The
        // opened file handle is compared with the exact fixed daily path before
        // any bytes are written, so a file link or directory redirection fails closed.
        // codeql[cs/path-injection]
        using var stream = new FileStream(
            expectedPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.WriteThrough);
        if (!WindowsPathIdentity.TryGetFinalPath(
                stream.SafeFileHandle,
                out string finalPath)
            || !string.Equals(
                expectedPath,
                finalPath,
                StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(stream.SafeFileHandle) & FileAttributes.ReparsePoint) != 0
            || !WindowsPathIdentity.TryGetHardLinkCount(
                stream.SafeFileHandle,
                out uint linkCount)
            || linkCount != 1
            || stream.Length + payload.LongLength > MaximumDailyBytes)
        {
            return false;
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(payload);
        stream.Flush(flushToDisk: false);
        return true;
    }

    private static void CleanupExpiredLogsOnce(
        string trustedDirectory,
        DateTime utcNow)
    {
        if (Interlocked.Exchange(ref _cleanupAttempted, 1) != 0)
            return;

        if (!IsTrustedLogDirectory(trustedDirectory))
            return;

        DateTime cutoffUtc = utcNow.AddDays(-RetentionDays);
        // trustedDirectory is a direct, handle-resolved application child and
        // enumeration is limited to the fixed daily-log pattern at top level.
        // codeql[cs/path-injection]
        foreach (string candidate in Directory.EnumerateFiles(
                     trustedDirectory,
                     "operations-????-??-??.jsonl",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsTrustedLogDirectory(trustedDirectory))
                    return;
                WindowsPathIdentity.TryDeleteDirectFileOlderThan(
                    candidate,
                    trustedDirectory,
                    cutoffUtc);
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
        int? ItemCount,
        int? RelatedProcessId,
        int? ExitCode);
}
