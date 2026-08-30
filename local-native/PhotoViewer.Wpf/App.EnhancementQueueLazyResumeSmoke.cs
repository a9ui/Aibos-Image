using Microsoft.Data.Sqlite;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace PhotoViewer.Wpf;

public partial class App
{
    private void CaptureEnhancementQueueLazyResumeSmoke(string resultPath)
    {
        string resultFullPath = Path.GetFullPath(resultPath);
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resultFullPath.StartsWith(
                tempRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            Shutdown(1);
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        SqliteConnection? walFixtureConnection = null;
        try
        {
            string fixtureRoot = Path.Combine(
                Path.GetDirectoryName(resultFullPath)!,
                "queue-bootstrap-wal");
            Directory.CreateDirectory(fixtureRoot);
            string sqlitePath = Path.Combine(fixtureRoot, "jobs.sqlite3");
            walFixtureConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sqlitePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                }.ToString());
            walFixtureConnection.Open();
            string journalMode;
            using (SqliteCommand journal = walFixtureConnection.CreateCommand())
            {
                journal.CommandText = "PRAGMA journal_mode=WAL;";
                journalMode = Convert.ToString(journal.ExecuteScalar()) ?? "";
            }
            using (SqliteCommand seed = walFixtureConnection.CreateCommand())
            {
                seed.CommandText = """
                    PRAGMA wal_autocheckpoint=0;
                    CREATE TABLE queue_state (
                        id INTEGER PRIMARY KEY,
                        paused INTEGER NOT NULL,
                        queued_count INTEGER NOT NULL,
                        first_queue_order INTEGER NOT NULL
                    );
                    INSERT INTO queue_state (
                        id,
                        paused,
                        queued_count,
                        first_queue_order
                    ) VALUES (1, 1, 1, 43);
                    """;
                seed.ExecuteNonQuery();
            }
            string quickCheck;
            using (SqliteCommand check = walFixtureConnection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                quickCheck = Convert.ToString(check.ExecuteScalar()) ?? "";
            }
            bool walSidecarsPresent = File.Exists(sqlitePath + "-wal")
                && File.Exists(sqlitePath + "-shm");
            bool walFixtureValid = string.Equals(
                    journalMode,
                    "wal",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(quickCheck, "ok", StringComparison.Ordinal)
                && walSidecarsPresent;
            string ReadQueueSemanticState()
            {
                using SqliteCommand read = walFixtureConnection.CreateCommand();
                read.CommandText = """
                    SELECT paused, queued_count, first_queue_order
                    FROM queue_state
                    WHERE id = 1;
                    """;
                using SqliteDataReader row = read.ExecuteReader();
                if (!row.Read())
                    return "missing";
                return $"paused:{row.GetInt64(0)}|queued:{row.GetInt64(1)}|first-order:{row.GetInt64(2)}";
            }
            string initialQueueSemanticState = ReadQueueSemanticState();

            window = HiddenWindow();
            bool syntheticCompanionStarted = false;
            bool queuePaused = true;
            bool queueStoreRecovered = false;
            bool recoveryPreservedQueueState = false;
            int starterCalls = 0;
            int transportCalls = 0;
            int secureRequests = 0;
            int recoveryRequests = 0;
            int healthBeforeRecoveryRequests = 0;
            int queueResumeRequests = 0;
            int unexpectedRequests = 0;
            string? resumeBody = null;
            var explicitRequestOrder = new List<string>();

            window.ConfigureEnhancementCompanionAutoStartForSmoke(
                async (request, token) =>
                {
                    transportCalls++;
                    string route = request.RequestUri?.AbsolutePath ?? "";
                    if (request.Method == HttpMethod.Get
                        && route == "/api/enhance/identity")
                    {
                        if (!syntheticCompanionStarted)
                        {
                            throw new HttpRequestException(
                                "Synthetic lazy Companion is not listening.");
                        }
                        if (!request.Headers.TryGetValues(
                                "X-Aibos-Companion-Challenge",
                                out IEnumerable<string>? challenges))
                        {
                            unexpectedRequests++;
                            return LazyResumeJsonResponse(
                                HttpStatusCode.BadRequest,
                                new { error = "missing challenge" });
                        }
                        return LazyResumeJsonResponse(
                            HttpStatusCode.OK,
                            window.EnhancementCompanionIdentityPayloadForSmoke(
                                challenges.Single()));
                    }

                    if (request.Method != HttpMethod.Post
                        || route != "/api/enhance/secure")
                    {
                        unexpectedRequests++;
                        return LazyResumeJsonResponse(
                            HttpStatusCode.NotFound,
                            new { error = "unexpected outer route" });
                    }

                    secureRequests++;
                    EnhancementCompanionSecureRequestSmokeSnapshot? decoded =
                        await window.DecodeEnhancementCompanionSecureRequestForSmokeAsync(
                            request,
                            token);
                    if (decoded is null)
                    {
                        unexpectedRequests++;
                        return LazyResumeJsonResponse(
                            HttpStatusCode.BadRequest,
                            new { error = "invalid secure request" });
                    }

                    object payload;
                    if (decoded.Method == "GET"
                        && decoded.PathAndQuery.EndsWith(
                            "/api/enhance/health",
                            StringComparison.Ordinal))
                    {
                        explicitRequestOrder.Add("health");
                        if (!queueStoreRecovered)
                        {
                            healthBeforeRecoveryRequests++;
                            return window.EnhancementCompanionSecureResponseForSmoke(
                                request,
                                (int)HttpStatusCode.ServiceUnavailable,
                                new
                                {
                                    error = "WAL-backed queue store requires explicit recovery.",
                                });
                        }
                        payload = LazyResumeHealth(queuePaused);
                    }
                    else if (decoded.Method == "GET"
                        && decoded.PathAndQuery.EndsWith(
                            "/api/enhance/jobs",
                            StringComparison.Ordinal))
                    {
                        payload = new { version = 1, jobs = Array.Empty<object>() };
                    }
                    else if (decoded.Method == "POST"
                        && decoded.PathAndQuery.EndsWith(
                            "/api/enhance/queue/recover",
                            StringComparison.Ordinal)
                        && string.IsNullOrEmpty(decoded.BodyJson))
                    {
                        explicitRequestOrder.Add("recover");
                        recoveryRequests++;
                        recoveryPreservedQueueState = walFixtureValid
                            && queuePaused
                            && string.Equals(
                                ReadQueueSemanticState(),
                                initialQueueSemanticState,
                                StringComparison.Ordinal);
                        queueStoreRecovered = true;
                        payload = new { recovered = true };
                    }
                    else if (decoded.Method == "POST"
                        && decoded.PathAndQuery.EndsWith(
                            "/api/enhance/queue",
                            StringComparison.Ordinal))
                    {
                        resumeBody = decoded.BodyJson;
                        if (!string.Equals(
                                resumeBody,
                                "{\"paused\":false}",
                                StringComparison.Ordinal))
                        {
                            unexpectedRequests++;
                            return LazyResumeJsonResponse(
                                HttpStatusCode.BadRequest,
                                new { error = "invalid resume body" });
                        }
                        explicitRequestOrder.Add("resume");
                        queueResumeRequests++;
                        queuePaused = false;
                        payload = new { paused = false, pumpRunning = true };
                    }
                    else
                    {
                        unexpectedRequests++;
                        return LazyResumeJsonResponse(
                            HttpStatusCode.NotFound,
                            new { error = "unexpected inner route" });
                    }

                    using JsonDocument payloadDocument = JsonDocument.Parse(
                        JsonSerializer.Serialize(payload));
                    return window.EnhancementCompanionSecureResponseForSmoke(
                        request,
                        200,
                        payloadDocument.RootElement.Clone());
                },
                _ =>
                {
                    starterCalls++;
                    syntheticCompanionStarted = true;
                    return (true, "");
                });

            window.Show();
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                object result = new
                {
                    ok = false,
                    message = "Queue lazy Resume smoke did not complete.",
                };
                bool ok = false;
                try
                {
                    window.PrepareUnknownEnhancementQueueResumeForSmoke();
                    EnhancementJobsWorkspaceSmokeSnapshot before =
                        window.EnhancementJobsWorkspaceForSmoke();
                    bool passiveDidNotStart =
                        starterCalls == 0
                        && transportCalls == 0
                        && before.QueuePauseEnabled
                        && before.QueuePauseLabel == "接続して再開";

                    bool resumed = await window
                        .SetEnhancementQueuePausedForSmokeAsync(paused: false);
                    EnhancementJobsWorkspaceSmokeSnapshot after =
                        window.EnhancementJobsWorkspaceForSmoke();
                    int secureBeforeDuplicate = secureRequests;
                    bool duplicateAccepted = await window
                        .SetEnhancementQueuePausedForSmokeAsync(paused: false);
                    bool duplicateGuarded =
                        duplicateAccepted
                        && secureRequests == secureBeforeDuplicate
                        && starterCalls == 1;
                    bool explicitResumeExact =
                        resumed
                        && starterCalls == 1
                        && recoveryRequests == 1
                        && queueResumeRequests == 1
                        && resumeBody == "{\"paused\":false}"
                        && after.QueuePaused == false
                        && after.QueuePauseLabel == "一時停止"
                        && after.QueuePauseEnabled
                        && unexpectedRequests == 0;
                    int recoveryIndex = explicitRequestOrder.IndexOf("recover");
                    int firstHealthIndex = explicitRequestOrder.IndexOf("health");
                    int resumeIndex = explicitRequestOrder.IndexOf("resume");
                    bool recoveryBeforeHealth = recoveryIndex >= 0
                        && firstHealthIndex > recoveryIndex
                        && resumeIndex > firstHealthIndex
                        && healthBeforeRecoveryRequests == 0;
                    ok = passiveDidNotStart
                        && explicitResumeExact
                        && duplicateGuarded
                        && walFixtureValid
                        && recoveryPreservedQueueState
                        && recoveryBeforeHealth;
                    result = new
                    {
                        ok,
                        passiveDidNotStart,
                        explicitResumeExact,
                        duplicateGuarded,
                        starterCalls,
                        transportCalls,
                        secureRequests,
                        recoveryRequests,
                        healthBeforeRecoveryRequests,
                        queueResumeRequests,
                        unexpectedRequests,
                        walFixtureValid,
                        walSidecarsPresent,
                        recoveryPreservedQueueState,
                        recoveryBeforeHealth,
                        explicitRequestOrder,
                        actualCompanionStarted = false,
                    };
                }
                catch (Exception ex)
                {
                    result = new
                    {
                        ok = false,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                    };
                }
                finally
                {
                    try { window.Close(); } catch { }
                    try { walFixtureConnection?.Dispose(); } catch { }
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(resultFullPath)!);
                    File.WriteAllText(
                        resultFullPath,
                        JsonSerializer.Serialize(
                            result,
                            new JsonSerializerOptions { WriteIndented = true }));
                    Shutdown(ok ? 0 : 1);
                }
            }, DispatcherPriority.ApplicationIdle);
        }
        catch
        {
            try { window?.Close(); } catch { }
            try { walFixtureConnection?.Dispose(); } catch { }
            Shutdown(1);
        }
    }

    private static HttpResponseMessage LazyResumeJsonResponse(
        HttpStatusCode status,
        object body)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };

    private static object LazyResumeHealth(bool paused)
        => new
        {
            version = 1,
            generatedAt = "2026-08-26T00:00:00.000Z",
            status = "healthy",
            issues = Array.Empty<string>(),
            runtime = new
            {
                sourceRevision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sourceDirty = false,
                buildId = "lazy-resume-smoke",
                processId = 4242,
                serverStartedAtUtc = "2026-08-26T00:00:00.000Z",
            },
            jobs = new
            {
                counts = new
                {
                    queued = 1,
                    running = 0,
                    succeeded = 0,
                    failed = 0,
                    canceled = 0,
                    deleted = 0,
                },
                lastClaimAt = (string?)null,
                lastProgressAt = (string?)null,
                lastTerminalAt = (string?)null,
            },
            store = new
            {
                inventoryRevision = 1,
                catalogRevision = 1,
                queueOrderRevision = 1,
            },
            worker = new
            {
                paused,
                pumpRunning = !paused,
            },
            capabilities = new
            {
                queuedPhotorealSettingsUpdateV1 = false,
                photorealPromptControlsV2 = true,
                kreaAnimeToRealV1 = true,
                atomicImageEnqueueNext = true,
                terminalHistoryBatchDismissV1 = true,
                queuedJobsBatchCancelV1 = true,
                queuedJobsBatchReorderV1 = true,
                terminalHistoryTargetsV1 = true,
                terminalHistoryBatchRetryV1 = true,
            },
        };
}
