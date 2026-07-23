using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PhotoViewer.Wpf;

internal static class ParityContractRunner
{
    private const string Runtime = "wpf";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly UTF8Encoding StrictUtf8WithoutBom = new(false, true);
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static readonly Regex ContractIdPattern = new(
        "^PV-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex CaseIdPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedKinds = [
        "search-history-identity",
        "search-history-document",
        "album-document",
        "album-operations",
        "shared-settings-document",
        "recent-folder-authority",
    ];

    internal static int Run(string? contractPath, string? explicitTempRoot, string? receiptPath)
    {
        var receipt = new ParityContractReceipt();
        string? receiptFullPath = null;

        try
        {
            receiptFullPath = ValidateTempPath(receiptPath, "receipt path", requireNested: true);
            string tempRoot = ValidateTempPath(explicitTempRoot, "explicit temp root", requireNested: true);
            string contractFullPath = RequirePath(contractPath, "contract path");

            byte[] contractBytes = File.ReadAllBytes(contractFullPath);
            byte[] canonicalContractBytes = CanonicalizeContractBytes(contractBytes);
            receipt.ContractSha256 = Convert.ToHexString(SHA256.HashData(canonicalContractBytes)).ToLowerInvariant();

            using JsonDocument document = JsonDocument.Parse(canonicalContractBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out JsonElement schemaElement)
                && schemaElement.TryGetInt32(out int receiptSchemaVersion))
            {
                receipt.SchemaVersion = receiptSchemaVersion;
            }

            IReadOnlyList<ContractVector> contracts = ValidateContract(document.RootElement);
            receipt.ContractIds.AddRange(contracts.Select(static contract => contract.Id));
            receipt.CaseIds.AddRange(contracts.SelectMany(static contract =>
                contract.Cases.Select(contractCase => $"{contract.Id}/{contractCase.GetProperty("id").GetString()}")));

            string runtimeRoot = Path.Combine(tempRoot, Runtime);
            if (Directory.Exists(runtimeRoot) && Directory.EnumerateFileSystemEntries(runtimeRoot).Any())
                throw new InvalidDataException("explicit temp root already contains a non-empty wpf lane");
            Directory.CreateDirectory(runtimeRoot);

            foreach (ContractVector contract in contracts)
            {
                foreach (JsonElement contractCase in contract.Cases)
                {
                    string caseId = RequireString(contractCase, "id", $"{contract.Id} case");
                    string scope = $"{contract.Id}/{caseId}";
                    string caseRoot = Path.Combine(runtimeRoot, contract.Id, caseId);
                    Directory.CreateDirectory(caseRoot);
                    receipt.CasesRun++;
                    try
                    {
                        RunCase(contract.Id, contract.Kind, contractCase, caseRoot, receipt.Failures);
                    }
                    catch (Exception ex)
                    {
                        receipt.Failures.Add($"{scope}: runner error: {OneLine(ex.Message)}");
                    }
                }
            }

            if (receipt.CasesRun != receipt.CaseIds.Count)
                receipt.Failures.Add($"contract: ran {receipt.CasesRun} of {receipt.CaseIds.Count} declared cases");
        }
        catch (Exception ex)
        {
            receipt.Failures.Add($"contract: {OneLine(ex.Message)}");
        }
        finally
        {
            if (receiptFullPath is not null)
                WriteReceipt(receiptFullPath, receipt);
        }

        return receipt.Failures.Count == 0 ? 0 : 1;
    }

    private static IReadOnlyList<ContractVector> ValidateContract(JsonElement root)
    {
        RequireObject(root, "contract root");
        RequireProperties(root, "contract root", ["schemaVersion", "sourceOfTruth", "contracts"]);
        if (!root.GetProperty("schemaVersion").TryGetInt32(out int schemaVersion) || schemaVersion != 1)
            throw new InvalidDataException("schemaVersion must be exactly 1");
        if (RequireString(root, "sourceOfTruth", "contract root") != "docs/product-contract.md")
            throw new InvalidDataException("sourceOfTruth must be docs/product-contract.md");

        JsonElement contractElements = RequireArray(root, "contracts", "contract root", requireNonEmpty: true);
        var contracts = new List<ContractVector>();
        var seenContractIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement contract in contractElements.EnumerateArray())
        {
            RequireObject(contract, "contract entry");
            RequireProperties(contract, "contract entry", ["id", "kind", "cases"]);
            string id = RequireString(contract, "id", "contract entry");
            string kind = RequireString(contract, "kind", id);
            if (!ContractIdPattern.IsMatch(id))
                throw new InvalidDataException($"invalid contract id {id}");
            if (!seenContractIds.Add(id))
                throw new InvalidDataException($"duplicate contract id {id}");
            if (!AllowedKinds.Contains(kind))
                throw new InvalidDataException($"{id} has unsupported kind {kind}");

            JsonElement caseElements = RequireArray(contract, "cases", id, requireNonEmpty: true);
            var cases = new List<JsonElement>();
            var seenCaseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement contractCase in caseElements.EnumerateArray())
            {
                RequireObject(contractCase, $"{id} case");
                string caseId = RequireString(contractCase, "id", $"{id} case");
                if (!CaseIdPattern.IsMatch(caseId))
                    throw new InvalidDataException($"{id} has invalid case id {caseId}");
                if (!seenCaseIds.Add(caseId))
                    throw new InvalidDataException($"{id} has duplicate case id {caseId}");
                ValidateCaseShape(id, kind, contractCase);
                cases.Add(contractCase.Clone());
            }
            contracts.Add(new ContractVector(id, kind, cases));
        }
        return contracts;
    }

    private static void ValidateCaseShape(string contractId, string kind, JsonElement contractCase)
    {
        string scope = $"{contractId}/{RequireString(contractCase, "id", contractId)}";
        switch (kind)
        {
            case "search-history-identity":
                RequireProperties(contractCase, scope, ["id", "samples"]);
                JsonElement samples = RequireArray(contractCase, "samples", scope, requireNonEmpty: true);
                foreach (JsonElement sample in samples.EnumerateArray())
                {
                    RequireObject(sample, $"{scope} sample");
                    RequireProperties(sample, $"{scope} sample", ["input", "normalized", "comparisonKey"]);
                    _ = RequireString(sample, "input", scope);
                    _ = RequireString(sample, "normalized", scope);
                    _ = RequireString(sample, "comparisonKey", scope);
                }
                break;

            case "search-history-document":
                RequireProperties(
                    contractCase,
                    scope,
                    ["id", "initial", "expected"],
                    ["operations", "generatedCommits"]);
                ValidateInitial(contractCase.GetProperty("initial"), scope);
                if (contractCase.TryGetProperty("operations", out JsonElement searchOperations))
                    ValidateSearchOperations(searchOperations, scope);
                if (contractCase.TryGetProperty("generatedCommits", out JsonElement generated))
                {
                    RequireObject(generated, $"{scope} generatedCommits");
                    RequireProperties(generated, $"{scope} generatedCommits", ["prefix", "count", "pad"]);
                    _ = RequireString(generated, "prefix", scope);
                    _ = RequireNonNegativeInt(generated, "count", scope);
                    _ = RequireNonNegativeInt(generated, "pad", scope);
                }
                ValidateSearchExpected(contractCase.GetProperty("expected"), scope);
                break;

            case "album-document":
                RequireProperties(contractCase, scope, ["id", "initial", "operations", "expected"]);
                ValidateInitial(contractCase.GetProperty("initial"), scope);
                ValidateAlbumDocumentOperations(contractCase.GetProperty("operations"), scope);
                ValidateAlbumDocumentExpected(contractCase.GetProperty("expected"), scope);
                break;

            case "album-operations":
                RequireProperties(contractCase, scope, ["id", "initial", "operations", "expected"]);
                ValidateInitial(contractCase.GetProperty("initial"), scope);
                ValidateAlbumOperations(contractCase.GetProperty("operations"), scope);
                ValidateAlbumOperationsExpected(contractCase.GetProperty("expected"), scope);
                break;

            case "shared-settings-document":
                ValidateSharedSettingsCase(contractCase, scope);
                break;

            case "recent-folder-authority":
                ValidateRecentAuthorityCase(contractCase, scope);
                break;

            default:
                throw new InvalidDataException($"{scope} has unsupported kind {kind}");
        }
    }

    private static void ValidateInitial(JsonElement initial, string scope)
    {
        RequireObject(initial, $"{scope} initial");
        string mode = RequireString(initial, "mode", $"{scope} initial");
        switch (mode)
        {
            case "missing":
                RequireProperties(initial, $"{scope} initial", ["mode"]);
                break;
            case "raw":
                RequireProperties(initial, $"{scope} initial", ["mode", "text"]);
                _ = RequireString(initial, "text", scope);
                break;
            case "json":
                RequireProperties(initial, $"{scope} initial", ["mode", "document"]);
                RequireObject(initial.GetProperty("document"), $"{scope} initial document");
                break;
            case "bytes-base64":
                RequireProperties(initial, $"{scope} initial", ["mode", "base64"]);
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(
                        RequireString(initial, "base64", scope));
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"{scope} initial base64 is invalid",
                        ex);
                }
                if (decoded.Length > SharedJsonDocumentReader.MaxDocumentBytes + 1)
                    throw new InvalidDataException($"{scope} initial byte fixture is too large");
                break;
            case "generated-utf8":
                RequireProperties(
                    initial,
                    $"{scope} initial",
                    ["mode", "byteLength", "prefix", "fillByte", "suffix"]);
                int byteLength = RequireNonNegativeInt(initial, "byteLength", scope);
                string prefix = RequireString(initial, "prefix", scope);
                string suffix = RequireString(initial, "suffix", scope);
                int fillByte = RequireNonNegativeInt(initial, "fillByte", scope);
                if (byteLength > SharedJsonDocumentReader.MaxDocumentBytes + 1)
                    throw new InvalidDataException($"{scope} generated fixture is too large");
                if (fillByte > 0x7F)
                    throw new InvalidDataException($"{scope} fillByte must be ASCII");
                if (Utf8WithoutBom.GetByteCount(prefix) != prefix.Length
                    || Utf8WithoutBom.GetByteCount(suffix) != suffix.Length)
                    throw new InvalidDataException($"{scope} generated fixture framing must be ASCII");
                if (byteLength < prefix.Length + suffix.Length)
                    throw new InvalidDataException($"{scope} generated fixture is shorter than its framing");
                break;
            default:
                throw new InvalidDataException($"{scope} has unknown initial mode {mode}");
        }
    }

    private static void ValidateSearchOperations(JsonElement operations, string scope)
    {
        RequireArray(operations, $"{scope} operations");
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            RequireObject(operation, $"{scope} operation");
            string action = RequireString(operation, "action", $"{scope} operation");
            if (action is "commit" or "delete")
            {
                RequireProperties(operation, $"{scope} operation", ["action", "query"]);
                _ = RequireString(operation, "query", scope);
            }
            else if (action == "clear")
            {
                RequireProperties(operation, $"{scope} operation", ["action"]);
            }
            else
            {
                throw new InvalidDataException($"{scope} has unknown Search History action {action}");
            }
        }
    }

    private static void ValidateSearchExpected(JsonElement expected, string scope)
    {
        RequireObject(expected, $"{scope} expected");
        RequireProperties(
            expected,
            $"{scope} expected",
            [
                "initialSupported", "initialMalformed", "initialFutureVersion",
                "finalSupported", "finalMalformed", "finalFutureVersion", "fileExists",
                "statuses", "unknownRoot", "bytesUnchanged"
            ],
            ["entries", "entryWindow"]);
        RequireBoolean(expected, "initialSupported", scope);
        RequireBoolean(expected, "initialMalformed", scope);
        RequireBoolean(expected, "initialFutureVersion", scope);
        RequireBoolean(expected, "finalSupported", scope);
        RequireBoolean(expected, "finalMalformed", scope);
        RequireBoolean(expected, "finalFutureVersion", scope);
        RequireBoolean(expected, "fileExists", scope);
        ValidateStatuses(expected.GetProperty("statuses"), scope);
        RequireObject(expected.GetProperty("unknownRoot"), $"{scope} expected unknownRoot");
        RequireBoolean(expected, "bytesUnchanged", scope);
        bool hasEntries = expected.TryGetProperty("entries", out JsonElement entries);
        bool hasWindow = expected.TryGetProperty("entryWindow", out JsonElement window);
        if (hasEntries == hasWindow)
            throw new InvalidDataException($"{scope} expected must contain exactly one of entries or entryWindow");
        if (hasEntries)
            ValidateStringArray(entries, $"{scope} expected entries");
        if (hasWindow)
        {
            RequireObject(window, $"{scope} expected entryWindow");
            RequireProperties(window, $"{scope} expected entryWindow", ["count", "first", "last"]);
            _ = RequireNonNegativeInt(window, "count", scope);
            _ = RequireString(window, "first", scope);
            _ = RequireString(window, "last", scope);
        }
    }

    private static void ValidateStatuses(JsonElement statuses, string scope)
    {
        if (statuses.ValueKind == JsonValueKind.Array)
        {
            ValidateStringArray(statuses, $"{scope} statuses");
            return;
        }
        RequireObject(statuses, $"{scope} statuses");
        RequireProperties(statuses, $"{scope} statuses", ["all", "count"]);
        _ = RequireString(statuses, "all", scope);
        _ = RequireNonNegativeInt(statuses, "count", scope);
    }

    private static void ValidateAlbumDocumentOperations(JsonElement operations, string scope)
    {
        RequireArray(operations, $"{scope} operations");
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            RequireObject(operation, $"{scope} operation");
            if (RequireString(operation, "action", scope) != "create")
                throw new InvalidDataException($"{scope} album-document only supports create operations");
            RequireProperties(operation, $"{scope} operation", ["action", "name", "albumId"], ["expectedRevision"]);
            _ = RequireString(operation, "name", scope);
            _ = RequireString(operation, "albumId", scope);
            if (operation.TryGetProperty("expectedRevision", out _))
                _ = RequireNonNegativeLong(operation, "expectedRevision", scope);
        }
    }

    private static void ValidateAlbumDocumentExpected(JsonElement expected, string scope)
    {
        string[] properties = [
            "initialSupported", "initialExists", "initialMalformed", "initialFutureVersion",
            "initialRevision", "initialAlbumCount", "statuses", "finalRevision", "finalAlbumCount",
            "fileExists", "bytesUnchangedAfterRead", "bytesUnchangedAfterOperations", "unknownRoot",
        ];
        RequireObject(expected, $"{scope} expected");
        RequireProperties(expected, $"{scope} expected", properties);
        foreach (string name in new[] {
            "initialSupported", "initialExists", "initialMalformed", "initialFutureVersion",
            "fileExists", "bytesUnchangedAfterRead", "bytesUnchangedAfterOperations",
        })
        {
            RequireBoolean(expected, name, scope);
        }
        RequireNullableNonNegativeLong(expected, "initialRevision", scope);
        RequireNullableNonNegativeInt(expected, "initialAlbumCount", scope);
        ValidateStringArray(RequireArray(expected, "statuses", scope), $"{scope} statuses");
        RequireNullableNonNegativeLong(expected, "finalRevision", scope);
        RequireNullableNonNegativeInt(expected, "finalAlbumCount", scope);
        RequireObject(expected.GetProperty("unknownRoot"), $"{scope} expected unknownRoot");
    }

    private static void ValidateAlbumOperations(JsonElement operations, string scope)
    {
        RequireArray(operations, $"{scope} operations", requireNonEmpty: true);
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            RequireObject(operation, $"{scope} operation");
            string action = RequireString(operation, "action", scope);
            switch (action)
            {
                case "add":
                    RequireProperties(operation, $"{scope} operation", ["action", "albumId", "paths"], ["expectedRevision"]);
                    _ = RequireString(operation, "albumId", scope);
                    ValidateStringArray(RequireArray(operation, "paths", scope, requireNonEmpty: true), $"{scope} paths");
                    break;
                case "update":
                    RequireProperties(operation, $"{scope} operation", ["action", "albumId"], ["name", "pinned", "expectedRevision"]);
                    _ = RequireString(operation, "albumId", scope);
                    if (operation.TryGetProperty("name", out _)) _ = RequireString(operation, "name", scope);
                    if (operation.TryGetProperty("pinned", out _)) RequireBoolean(operation, "pinned", scope);
                    break;
                case "cleanupPaths":
                    RequireProperties(operation, $"{scope} operation", ["action", "paths"], ["expectedRevision"]);
                    ValidateStringArray(RequireArray(operation, "paths", scope, requireNonEmpty: true), $"{scope} paths");
                    break;
                default:
                    throw new InvalidDataException($"{scope} has unknown Album action {action}");
            }
            if (operation.TryGetProperty("expectedRevision", out _))
                _ = RequireNonNegativeLong(operation, "expectedRevision", scope);
        }
    }

    private static void ValidateAlbumOperationsExpected(JsonElement expected, string scope)
    {
        RequireObject(expected, $"{scope} expected");
        RequireProperties(expected, $"{scope} expected", [
            "initialSupported", "initialExists", "initialMalformed", "initialFutureVersion",
            "initialRevision", "initialAlbumCount", "statuses", "changed", "revisions",
            "finalRevision", "finalAlbumCount", "fileExists", "bytesUnchangedAfterRead",
            "bytesUnchangedAfterOperations", "finalAlbum",
            "unknownRoot", "unknownAlbum", "unknownMember",
        ]);
        foreach (string name in new[] {
            "initialSupported", "initialExists", "initialMalformed", "initialFutureVersion",
            "fileExists", "bytesUnchangedAfterRead", "bytesUnchangedAfterOperations",
        })
        {
            RequireBoolean(expected, name, scope);
        }
        RequireNullableNonNegativeLong(expected, "initialRevision", scope);
        RequireNullableNonNegativeInt(expected, "initialAlbumCount", scope);
        ValidateStringArray(RequireArray(expected, "statuses", scope), $"{scope} statuses");
        ValidateBooleanArray(RequireArray(expected, "changed", scope), $"{scope} changed");
        ValidateLongArray(RequireArray(expected, "revisions", scope), $"{scope} revisions");
        _ = RequireNonNegativeLong(expected, "finalRevision", scope);
        _ = RequireNonNegativeInt(expected, "finalAlbumCount", scope);
        JsonElement finalAlbum = expected.GetProperty("finalAlbum");
        RequireObject(finalAlbum, $"{scope} finalAlbum");
        RequireProperties(finalAlbum, $"{scope} finalAlbum", [
            "id", "name", "pinned", "coverMemberId", "revision", "memberPaths",
        ]);
        _ = RequireString(finalAlbum, "id", scope);
        _ = RequireString(finalAlbum, "name", scope);
        RequireBoolean(finalAlbum, "pinned", scope);
        RequireNullableString(finalAlbum, "coverMemberId", scope);
        _ = RequireNonNegativeLong(finalAlbum, "revision", scope);
        ValidateStringArray(RequireArray(finalAlbum, "memberPaths", scope), $"{scope} memberPaths");
        RequireObject(expected.GetProperty("unknownRoot"), $"{scope} unknownRoot");
        RequireObject(expected.GetProperty("unknownAlbum"), $"{scope} unknownAlbum");
        JsonElement unknownMember = expected.GetProperty("unknownMember");
        RequireObject(unknownMember, $"{scope} unknownMember");
        RequireProperties(unknownMember, $"{scope} unknownMember", ["memberId", "fields"]);
        _ = RequireString(unknownMember, "memberId", scope);
        RequireObject(unknownMember.GetProperty("fields"), $"{scope} unknownMember fields");
    }

    private static void ValidateSharedSettingsCase(JsonElement contractCase, string scope)
    {
        RequireProperties(
            contractCase,
            scope,
            ["id", "initial", "localConfirmBeforeDelete", "operations", "expected"]);
        ValidateInitial(contractCase.GetProperty("initial"), scope);
        _ = contractCase.GetProperty("localConfirmBeforeDelete").GetBoolean();
        JsonElement operations = RequireArray(
            contractCase,
            "operations",
            scope,
            requireNonEmpty: true);
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            RequireObject(operation, $"{scope} operation");
            string action = RequireString(operation, "action", scope);
            if (action == "confirm")
            {
                RequireProperties(operation, $"{scope} confirm operation", ["action", "value"]);
                _ = operation.GetProperty("value").GetBoolean();
                continue;
            }
            if (action == "borders")
            {
                RequireProperties(
                    operation,
                    $"{scope} borders operation",
                    ["action", "dirty", "favorite", "enhanced"]);
                JsonElement dirty = RequireArray(
                    operation,
                    "dirty",
                    scope,
                    requireNonEmpty: true);
                foreach (JsonElement item in dirty.EnumerateArray())
                {
                    string name = item.GetString()
                        ?? throw new InvalidDataException($"{scope} dirty entry must be a string");
                    if (name is not ("favorite" or "enhanced"))
                        throw new InvalidDataException($"{scope} has unknown dirty setting {name}");
                }
                ValidateBorderPreference(operation.GetProperty("favorite"), $"{scope} favorite");
                ValidateBorderPreference(operation.GetProperty("enhanced"), $"{scope} enhanced");
                continue;
            }
            throw new InvalidDataException($"{scope} has unknown settings action {action}");
        }

        JsonElement expected = contractCase.GetProperty("expected");
        RequireObject(expected, $"{scope} expected");
        RequireProperties(
            expected,
            $"{scope} expected",
            [
                "initialProtected",
                "effectiveConfirmBeforeDelete",
                "statuses",
                "fileExists",
                "bytesUnchanged",
                "final",
            ]);
        _ = expected.GetProperty("initialProtected").GetBoolean();
        _ = expected.GetProperty("effectiveConfirmBeforeDelete").GetBoolean();
        _ = RequireArray(expected, "statuses", scope, requireNonEmpty: true);
        _ = expected.GetProperty("fileExists").GetBoolean();
        _ = expected.GetProperty("bytesUnchanged").GetBoolean();
        ValidateExpectedFinalDocument(expected.GetProperty("final"), scope);
    }

    private static void ValidateBorderPreference(JsonElement preference, string scope)
    {
        RequireObject(preference, scope);
        RequireProperties(preference, scope, ["enabled", "color"]);
        _ = preference.GetProperty("enabled").GetBoolean();
        _ = RequireString(preference, "color", scope);
    }

    private static void ValidateExpectedFinalDocument(JsonElement final, string scope)
    {
        RequireObject(final, $"{scope} final");
        string mode = RequireString(final, "mode", $"{scope} final");
        switch (mode)
        {
            case "missing":
                RequireProperties(final, $"{scope} final", ["mode"]);
                break;
            case "raw":
                RequireProperties(final, $"{scope} final", ["mode", "text"]);
                _ = RequireString(final, "text", scope);
                break;
            case "json":
                RequireProperties(final, $"{scope} final", ["mode", "document"]);
                RequireObject(final.GetProperty("document"), $"{scope} final document");
                break;
            case "bytes-base64":
                RequireProperties(final, $"{scope} final", ["mode", "base64"]);
                try
                {
                    _ = Convert.FromBase64String(
                        RequireString(final, "base64", scope));
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"{scope} final base64 is invalid",
                        ex);
                }
                break;
            case "generated-utf8":
                RequireProperties(
                    final,
                    $"{scope} final",
                    ["mode", "byteLength", "prefix", "fillByte", "suffix"]);
                int byteLength = RequireNonNegativeInt(final, "byteLength", scope);
                string prefix = RequireString(final, "prefix", scope);
                string suffix = RequireString(final, "suffix", scope);
                int fillByte = RequireNonNegativeInt(final, "fillByte", scope);
                if (byteLength > SharedJsonDocumentReader.MaxDocumentBytes + 1
                    || fillByte > 0x7F
                    || Utf8WithoutBom.GetByteCount(prefix) != prefix.Length
                    || Utf8WithoutBom.GetByteCount(suffix) != suffix.Length
                    || byteLength < prefix.Length + suffix.Length)
                    throw new InvalidDataException($"{scope} final generated fixture is invalid");
                break;
            default:
                throw new InvalidDataException($"{scope} has unknown final mode {mode}");
        }
    }

    private static void ValidateRecentAuthorityCase(JsonElement contractCase, string scope)
    {
        RequireProperties(
            contractCase,
            scope,
            ["id", "initial", "localFolderSet", "expected"],
            ["operations"]);
        ValidateInitial(contractCase.GetProperty("initial"), scope);
        JsonElement local = RequireArray(
            contractCase,
            "localFolderSet",
            scope,
            requireNonEmpty: false);
        foreach (JsonElement item in local.EnumerateArray())
            _ = item.GetString() ?? throw new InvalidDataException($"{scope} local folder must be a string");
        if (contractCase.TryGetProperty("operations", out JsonElement operations))
        {
            foreach (JsonElement operation in RequireArray(
                         contractCase,
                         "operations",
                         scope,
                         requireNonEmpty: true).EnumerateArray())
            {
                RequireObject(operation, $"{scope} operation");
                RequireProperties(operation, $"{scope} operation", ["action", "folder"]);
                if (RequireString(operation, "action", scope) != "merge")
                    throw new InvalidDataException($"{scope} has an unsupported Recent operation");
                _ = RequireString(operation, "folder", scope);
            }
        }

        JsonElement expected = contractCase.GetProperty("expected");
        RequireObject(expected, $"{scope} expected");
        RequireProperties(
            expected,
            $"{scope} expected",
            ["readOk", "exists", "selectedFolderSet", "fileExists", "bytesUnchanged"],
            ["statuses", "canonicalUtf8WithoutBom", "unknownRoot"]);
        _ = expected.GetProperty("readOk").GetBoolean();
        _ = expected.GetProperty("exists").GetBoolean();
        JsonElement selected = RequireArray(
            expected,
            "selectedFolderSet",
            scope,
            requireNonEmpty: false);
        foreach (JsonElement item in selected.EnumerateArray())
            _ = item.GetString() ?? throw new InvalidDataException($"{scope} expected folder must be a string");
        _ = expected.GetProperty("fileExists").GetBoolean();
        _ = expected.GetProperty("bytesUnchanged").GetBoolean();
        if (expected.TryGetProperty("statuses", out JsonElement statuses))
            ValidateStringArray(statuses, $"{scope} statuses");
        if (expected.TryGetProperty("canonicalUtf8WithoutBom", out _))
            RequireBoolean(expected, "canonicalUtf8WithoutBom", scope);
        if (expected.TryGetProperty("unknownRoot", out JsonElement unknownRoot))
            RequireObject(unknownRoot, $"{scope} expected unknownRoot");
    }

    private static void RunCase(
        string contractId,
        string kind,
        JsonElement contractCase,
        string caseRoot,
        List<string> failures)
    {
        string caseId = RequireString(contractCase, "id", contractId);
        string scope = $"{contractId}/{caseId}";
        switch (kind)
        {
            case "search-history-identity":
                RunSearchIdentity(contractCase, scope, failures);
                break;
            case "search-history-document":
                RunSearchDocument(contractCase, scope, caseRoot, failures);
                break;
            case "album-document":
                RunAlbumDocument(contractCase, scope, caseRoot, failures);
                break;
            case "album-operations":
                RunAlbumOperations(contractCase, scope, caseRoot, failures);
                break;
            case "shared-settings-document":
                RunSharedSettingsDocument(contractCase, scope, caseRoot, failures);
                break;
            case "recent-folder-authority":
                RunRecentFolderAuthority(contractCase, scope, caseRoot, failures);
                break;
            default:
                throw new InvalidDataException($"unsupported kind {kind}");
        }
    }

    private static void RunSearchIdentity(JsonElement contractCase, string scope, List<string> failures)
    {
        int sampleIndex = 0;
        foreach (JsonElement sample in contractCase.GetProperty("samples").EnumerateArray())
        {
            string input = sample.GetProperty("input").GetString()!;
            string normalized = SearchHistoryStore.NormalizeQuery(input);
            string comparisonKey = SearchHistoryStore.ComparisonKey(input);
            Expect(failures, scope, normalized == sample.GetProperty("normalized").GetString(), $"sample {sampleIndex} normalized mismatch");
            Expect(failures, scope, comparisonKey == sample.GetProperty("comparisonKey").GetString(), $"sample {sampleIndex} comparisonKey mismatch");
            sampleIndex++;
        }
    }

    private static void RunSharedSettingsDocument(
        JsonElement contractCase,
        string scope,
        string caseRoot,
        List<string> failures)
    {
        string storePath = Path.Combine(caseRoot, "settings.json");
        PrepareInitial(storePath, contractCase.GetProperty("initial"), caseRoot);
        byte[]? initialBytes = ReadOptionalBytes(storePath);
        ThumbnailStatusBorderLoadResult initial =
            ThumbnailStatusBorderSettingsStore.Read(storePath);
        JsonElement expected = contractCase.GetProperty("expected");
        bool effectiveConfirmBeforeDelete =
            ThumbnailStatusBorderSettingsStore.ResolveEffectiveConfirmBeforeDelete(
                contractCase.GetProperty("localConfirmBeforeDelete").GetBoolean(),
                initial);
        Expect(
            failures,
            scope,
            initial.IsProtected == expected.GetProperty("initialProtected").GetBoolean(),
            "initialProtected mismatch");
        Expect(
            failures,
            scope,
            effectiveConfirmBeforeDelete
                == expected.GetProperty("effectiveConfirmBeforeDelete").GetBoolean(),
            "effectiveConfirmBeforeDelete mismatch");

        var statuses = new List<string>();
        foreach (JsonElement operation in contractCase.GetProperty("operations").EnumerateArray())
        {
            if (!ThumbnailStatusBorderSettingsStore.TryReadExistingJson(
                    storePath,
                    out string? existingJson,
                    out _))
            {
                statuses.Add("Protected");
                continue;
            }
            string action = operation.GetProperty("action").GetString()!;
            bool merged;
            string mergedJson;
            if (action == "confirm")
            {
                merged = ThumbnailStatusBorderSettingsStore.TryMergeConfirmBeforeDelete(
                    existingJson,
                    operation.GetProperty("value").GetBoolean(),
                    out mergedJson,
                    out _);
            }
            else
            {
                ThumbnailStatusBorderDirtyPreferences dirty =
                    ThumbnailStatusBorderDirtyPreferences.None;
                foreach (JsonElement name in operation.GetProperty("dirty").EnumerateArray())
                {
                    dirty |= name.GetString() switch
                    {
                        "favorite" => ThumbnailStatusBorderDirtyPreferences.Favorite,
                        "enhanced" => ThumbnailStatusBorderDirtyPreferences.Enhanced,
                        _ => ThumbnailStatusBorderDirtyPreferences.None,
                    };
                }
                JsonElement favorite = operation.GetProperty("favorite");
                JsonElement enhanced = operation.GetProperty("enhanced");
                var settings = new ThumbnailStatusBorderSettings(
                    new ThumbnailStatusBorderPreference(
                        favorite.GetProperty("enabled").GetBoolean(),
                        favorite.GetProperty("color").GetString()!),
                    new ThumbnailStatusBorderPreference(
                        enhanced.GetProperty("enabled").GetBoolean(),
                        enhanced.GetProperty("color").GetString()!));
                merged = ThumbnailStatusBorderSettingsStore.TryMerge(
                    existingJson,
                    settings,
                    dirty,
                    out mergedJson,
                    out _);
            }

            statuses.Add(merged ? "Succeeded" : "Protected");
            if (merged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
                File.WriteAllText(storePath, mergedJson, Utf8WithoutBom);
            }
        }

        AssertStringArray(statuses, expected.GetProperty("statuses"), scope, "statuses", failures);
        Expect(
            failures,
            scope,
            File.Exists(storePath) == expected.GetProperty("fileExists").GetBoolean(),
            "fileExists mismatch");
        Expect(
            failures,
            scope,
            OptionalBytesEqual(initialBytes, ReadOptionalBytes(storePath))
                == expected.GetProperty("bytesUnchanged").GetBoolean(),
            "byte-preservation expectation failed");
        AssertExpectedFinalDocument(
            storePath,
            expected.GetProperty("final"),
            caseRoot,
            scope,
            failures);
    }

    private static void RunRecentFolderAuthority(
        JsonElement contractCase,
        string scope,
        string caseRoot,
        List<string> failures)
    {
        string storePath = Path.Combine(caseRoot, "recent-folders.json");
        PrepareInitial(storePath, contractCase.GetProperty("initial"), caseRoot);
        byte[]? initialBytes = ReadOptionalBytes(storePath);
        SharedRecentReadResult read = MainWindow.ReadSharedRecentFoldersForSmoke(storePath);
        IReadOnlyList<string> local = ReadExpandedPaths(
            contractCase.GetProperty("localFolderSet"),
            caseRoot);
        IReadOnlyList<string> selected = MainWindow.ResolveStartupFolderSetForSmoke(
            storePath,
            local,
            null);
        JsonElement expected = contractCase.GetProperty("expected");
        Expect(
            failures,
            scope,
            read.Ok == expected.GetProperty("readOk").GetBoolean(),
            "readOk mismatch");
        Expect(
            failures,
            scope,
            read.Exists == expected.GetProperty("exists").GetBoolean(),
            "exists mismatch");
        ExpectPathSequence(
            failures,
            scope,
            selected,
            ReadExpandedPaths(expected.GetProperty("selectedFolderSet"), caseRoot),
            "selectedFolderSet");
        if (contractCase.TryGetProperty("operations", out JsonElement operations))
        {
            var statuses = new List<string>();
            foreach (JsonElement operation in operations.EnumerateArray())
            {
                bool merged = MainWindow.TryMergeSharedRecentForSmoke(
                    storePath,
                    ExpandString(operation.GetProperty("folder").GetString()!, caseRoot));
                statuses.Add(merged ? "Succeeded" : "Protected");
            }
            AssertStringArray(
                statuses,
                expected.GetProperty("statuses"),
                scope,
                "statuses",
                failures);
        }
        Expect(
            failures,
            scope,
            File.Exists(storePath) == expected.GetProperty("fileExists").GetBoolean(),
            "fileExists mismatch");
        Expect(
            failures,
            scope,
            OptionalBytesEqual(initialBytes, ReadOptionalBytes(storePath))
                == expected.GetProperty("bytesUnchanged").GetBoolean(),
            "byte-preservation expectation failed");
        if (expected.TryGetProperty("canonicalUtf8WithoutBom", out JsonElement canonicalExpected)
            || expected.TryGetProperty("unknownRoot", out _))
        {
            if (!File.Exists(storePath))
            {
                failures.Add($"{scope}: final Recent document is missing");
            }
            else
            {
                byte[] finalBytes = File.ReadAllBytes(storePath);
                try
                {
                    _ = StrictUtf8WithoutBom.GetString(finalBytes);
                    if (canonicalExpected.ValueKind != JsonValueKind.Undefined)
                    {
                        bool hasBom = finalBytes.Length >= 3
                            && finalBytes[0] == 0xEF
                            && finalBytes[1] == 0xBB
                            && finalBytes[2] == 0xBF;
                        Expect(
                            failures,
                            scope,
                            !hasBom == canonicalExpected.GetBoolean(),
                            "canonical UTF-8 BOM expectation failed");
                    }
                    if (expected.TryGetProperty("unknownRoot", out JsonElement unknownExpected))
                    {
                        using JsonDocument final = JsonDocument.Parse(finalBytes);
                        AssertUnknownObject(
                            final.RootElement,
                            unknownExpected,
                            ["version", "lastFolderSet", "recentFolderSets", "updatedAtUtc"],
                            scope,
                            "unknownRoot",
                            failures);
                    }
                }
                catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
                {
                    failures.Add($"{scope}: final Recent encoding or JSON was invalid: {ex.Message}");
                }
            }
        }
    }

    private static void RunSearchDocument(JsonElement contractCase, string scope, string caseRoot, List<string> failures)
    {
        string storePath = Path.Combine(caseRoot, "search-history.json");
        PrepareInitial(storePath, contractCase.GetProperty("initial"), caseRoot);
        byte[]? initialBytes = ReadOptionalBytes(storePath);
        SearchHistoryReadResult initial = SearchHistoryStore.Read(storePath);
        JsonElement expected = contractCase.GetProperty("expected");
        Expect(failures, scope, initial.Supported == expected.GetProperty("initialSupported").GetBoolean(), "initialSupported mismatch");
        Expect(failures, scope, initial.Malformed == expected.GetProperty("initialMalformed").GetBoolean(), "initialMalformed mismatch");
        Expect(failures, scope, initial.FutureVersion == expected.GetProperty("initialFutureVersion").GetBoolean(), "initialFutureVersion mismatch");

        var statuses = new List<string>();
        if (contractCase.TryGetProperty("operations", out JsonElement operations))
        {
            foreach (JsonElement operation in operations.EnumerateArray())
            {
                string action = operation.GetProperty("action").GetString()!;
                SearchHistoryWriteResult result = action switch
                {
                    "commit" => SearchHistoryStore.Commit(storePath, operation.GetProperty("query").GetString()!),
                    "delete" => SearchHistoryStore.Delete(storePath, operation.GetProperty("query").GetString()!),
                    "clear" => SearchHistoryStore.Clear(storePath),
                    _ => throw new InvalidDataException($"unsupported Search History action {action}"),
                };
                statuses.Add(result.Status.ToString());
            }
        }
        if (contractCase.TryGetProperty("generatedCommits", out JsonElement generated))
        {
            string prefix = generated.GetProperty("prefix").GetString()!;
            int count = generated.GetProperty("count").GetInt32();
            int pad = generated.GetProperty("pad").GetInt32();
            for (int index = 0; index < count; index++)
            {
                SearchHistoryWriteResult result = SearchHistoryStore.Commit(storePath, prefix + index.ToString().PadLeft(pad, '0'));
                statuses.Add(result.Status.ToString());
            }
        }

        AssertStatuses(statuses, expected.GetProperty("statuses"), scope, failures);
        SearchHistoryReadResult final = SearchHistoryStore.Read(storePath);
        Expect(failures, scope, final.Supported == expected.GetProperty("finalSupported").GetBoolean(), "finalSupported mismatch");
        Expect(failures, scope, final.Malformed == expected.GetProperty("finalMalformed").GetBoolean(), "finalMalformed mismatch");
        Expect(failures, scope, final.FutureVersion == expected.GetProperty("finalFutureVersion").GetBoolean(), "finalFutureVersion mismatch");
        Expect(failures, scope, File.Exists(storePath) == expected.GetProperty("fileExists").GetBoolean(), "fileExists mismatch");
        if (expected.TryGetProperty("entries", out JsonElement expectedEntries))
        {
            ExpectSequence(failures, scope, final.Entries, expectedEntries.EnumerateArray().Select(static item => item.GetString()!).ToArray(), "entries");
        }
        else
        {
            JsonElement window = expected.GetProperty("entryWindow");
            int count = window.GetProperty("count").GetInt32();
            Expect(failures, scope, final.Entries.Count == count, $"entry count expected {count}, got {final.Entries.Count}");
            if (final.Entries.Count > 0)
            {
                Expect(failures, scope, final.Entries[0] == window.GetProperty("first").GetString(), "entryWindow first mismatch");
                Expect(failures, scope, final.Entries[^1] == window.GetProperty("last").GetString(), "entryWindow last mismatch");
            }
        }

        AssertUnknownRoot(
            storePath,
            expected.GetProperty("unknownRoot"),
            ["version", "entries", "updatedAtUtc"],
            final.Supported,
            scope,
            failures);
        bool bytesUnchanged = OptionalBytesEqual(initialBytes, ReadOptionalBytes(storePath));
        Expect(failures, scope, bytesUnchanged == expected.GetProperty("bytesUnchanged").GetBoolean(), "byte-preservation expectation failed");
    }

    private static void RunAlbumDocument(JsonElement contractCase, string scope, string caseRoot, List<string> failures)
    {
        string storePath = Path.Combine(caseRoot, "albums.json");
        PrepareInitial(storePath, contractCase.GetProperty("initial"), caseRoot);
        byte[]? initialBytes = ReadOptionalBytes(storePath);
        AlbumReadResult initial = AlbumStore.Read(storePath);
        byte[]? afterReadBytes = ReadOptionalBytes(storePath);
        JsonElement expected = contractCase.GetProperty("expected");

        Expect(failures, scope, initial.Supported == expected.GetProperty("initialSupported").GetBoolean(), "initialSupported mismatch");
        Expect(failures, scope, initial.Exists == expected.GetProperty("initialExists").GetBoolean(), "initialExists mismatch");
        Expect(failures, scope, initial.Malformed == expected.GetProperty("initialMalformed").GetBoolean(), "initialMalformed mismatch");
        Expect(failures, scope, initial.FutureVersion == expected.GetProperty("initialFutureVersion").GetBoolean(), "initialFutureVersion mismatch");
        AssertNullableLong(initial.Document?.Revision, expected.GetProperty("initialRevision"), scope, "initialRevision", failures);
        AssertNullableInt(initial.Document?.Albums.Count, expected.GetProperty("initialAlbumCount"), scope, "initialAlbumCount", failures);
        Expect(failures, scope, OptionalBytesEqual(initialBytes, afterReadBytes) == expected.GetProperty("bytesUnchangedAfterRead").GetBoolean(), "read byte-preservation expectation failed");

        var statuses = new List<string>();
        foreach (JsonElement operation in contractCase.GetProperty("operations").EnumerateArray())
        {
            long? expectedRevision = OptionalLong(operation, "expectedRevision");
            AlbumMutationResult result = AlbumStore.Create(
                storePath,
                operation.GetProperty("name").GetString()!,
                expectedRevision,
                operation.GetProperty("albumId").GetString()!);
            statuses.Add(result.Status.ToString());
        }
        AssertStringArray(statuses, expected.GetProperty("statuses"), scope, "statuses", failures);

        AlbumReadResult final = AlbumStore.Read(storePath);
        AssertNullableLong(final.Document?.Revision, expected.GetProperty("finalRevision"), scope, "finalRevision", failures);
        AssertNullableInt(final.Document?.Albums.Count, expected.GetProperty("finalAlbumCount"), scope, "finalAlbumCount", failures);
        Expect(failures, scope, File.Exists(storePath) == expected.GetProperty("fileExists").GetBoolean(), "fileExists mismatch");
        Expect(failures, scope, OptionalBytesEqual(initialBytes, ReadOptionalBytes(storePath)) == expected.GetProperty("bytesUnchangedAfterOperations").GetBoolean(), "mutation byte-preservation expectation failed");
        AssertUnknownRoot(
            storePath,
            expected.GetProperty("unknownRoot"),
            ["version", "revision", "updatedAtUtc", "albums", "recentAlbumIds"],
            final.Supported,
            scope,
            failures);
    }

    private static void RunAlbumOperations(JsonElement contractCase, string scope, string caseRoot, List<string> failures)
    {
        string storePath = Path.Combine(caseRoot, "albums.json");
        PrepareInitial(storePath, contractCase.GetProperty("initial"), caseRoot);
        byte[]? initialBytes = ReadOptionalBytes(storePath);
        AlbumReadResult initial = AlbumStore.Read(storePath);
        byte[]? afterReadBytes = ReadOptionalBytes(storePath);
        JsonElement expected = contractCase.GetProperty("expected");
        Expect(failures, scope, initial.Supported == expected.GetProperty("initialSupported").GetBoolean(), "initialSupported mismatch");
        Expect(failures, scope, initial.Exists == expected.GetProperty("initialExists").GetBoolean(), "initialExists mismatch");
        Expect(failures, scope, initial.Malformed == expected.GetProperty("initialMalformed").GetBoolean(), "initialMalformed mismatch");
        Expect(failures, scope, initial.FutureVersion == expected.GetProperty("initialFutureVersion").GetBoolean(), "initialFutureVersion mismatch");
        AssertNullableLong(initial.Document?.Revision, expected.GetProperty("initialRevision"), scope, "initialRevision", failures);
        AssertNullableInt(initial.Document?.Albums.Count, expected.GetProperty("initialAlbumCount"), scope, "initialAlbumCount", failures);
        Expect(failures, scope, OptionalBytesEqual(initialBytes, afterReadBytes) == expected.GetProperty("bytesUnchangedAfterRead").GetBoolean(), "read byte-preservation expectation failed");
        var statuses = new List<string>();
        var changed = new List<bool>();
        var revisions = new List<long?>();

        foreach (JsonElement operation in contractCase.GetProperty("operations").EnumerateArray())
        {
            string action = operation.GetProperty("action").GetString()!;
            long? expectedRevision = OptionalLong(operation, "expectedRevision");
            AlbumMutationResult result = action switch
            {
                "add" => AlbumStore.AddMembers(
                    storePath,
                    operation.GetProperty("albumId").GetString()!,
                    ReadExpandedPaths(operation.GetProperty("paths"), caseRoot),
                    expectedRevision),
                "update" => AlbumStore.Update(
                    storePath,
                    operation.GetProperty("albumId").GetString()!,
                    expectedRevision,
                    operation.TryGetProperty("name", out JsonElement name) ? name.GetString() : null,
                    operation.TryGetProperty("pinned", out JsonElement pinned) ? pinned.GetBoolean() : null),
                "cleanupPaths" => AlbumStore.CleanupPaths(
                    storePath,
                    ReadExpandedPaths(operation.GetProperty("paths"), caseRoot),
                    expectedRevision),
                _ => throw new InvalidDataException($"unsupported Album action {action}"),
            };
            statuses.Add(result.Status.ToString());
            changed.Add(result.Changed);
            revisions.Add(result.Document?.Revision);
        }

        AssertStringArray(statuses, expected.GetProperty("statuses"), scope, "statuses", failures);
        AssertBooleanArray(changed, expected.GetProperty("changed"), scope, "changed", failures);
        AssertNullableLongArray(revisions, expected.GetProperty("revisions"), scope, "revisions", failures);

        AlbumReadResult final = AlbumStore.Read(storePath);
        long expectedRevisionValue = expected.GetProperty("finalRevision").GetInt64();
        string actualRevision = final.Document is null ? "null" : final.Document.Revision.ToString();
        Expect(failures, scope, final.Document?.Revision == expectedRevisionValue, $"finalRevision expected {expectedRevisionValue}, got {actualRevision}");
        int expectedAlbumCount = expected.GetProperty("finalAlbumCount").GetInt32();
        Expect(failures, scope, final.Document?.Albums.Count == expectedAlbumCount, $"finalAlbumCount expected {expectedAlbumCount}, got {final.Document?.Albums.Count.ToString() ?? "null"}");
        Expect(failures, scope, File.Exists(storePath) == expected.GetProperty("fileExists").GetBoolean(), "fileExists mismatch");
        Expect(failures, scope, OptionalBytesEqual(initialBytes, ReadOptionalBytes(storePath)) == expected.GetProperty("bytesUnchangedAfterOperations").GetBoolean(), "mutation byte-preservation expectation failed");

        JsonElement expectedAlbum = expected.GetProperty("finalAlbum");
        string albumId = expectedAlbum.GetProperty("id").GetString()!;
        AlbumEntry? album = final.Document?.Albums.FirstOrDefault(item => item.Id == albumId);
        Expect(failures, scope, album is not null, $"final Album {albumId} missing");
        if (album is not null)
        {
            Expect(failures, scope, album.Name == expectedAlbum.GetProperty("name").GetString(), "final Album name mismatch");
            Expect(failures, scope, album.Pinned == expectedAlbum.GetProperty("pinned").GetBoolean(), "final Album pinned mismatch");
            string? expectedCover = expectedAlbum.GetProperty("coverMemberId").ValueKind == JsonValueKind.Null
                ? null
                : expectedAlbum.GetProperty("coverMemberId").GetString();
            Expect(failures, scope, album.CoverMemberId == expectedCover, "final Album coverMemberId mismatch");
            Expect(failures, scope, album.Revision == expectedAlbum.GetProperty("revision").GetInt64(), "final Album revision mismatch");
            IReadOnlyList<string> expectedPaths = ReadExpandedPaths(expectedAlbum.GetProperty("memberPaths"), caseRoot);
            ExpectPathSequence(failures, scope, album.Members.Select(static member => member.ImagePath).ToArray(), expectedPaths, "final Album memberPaths");
        }

        using JsonDocument finalJson = JsonDocument.Parse(File.ReadAllBytes(storePath));
        JsonElement root = finalJson.RootElement;
        AssertUnknownObject(root, expected.GetProperty("unknownRoot"), ["version", "revision", "updatedAtUtc", "albums", "recentAlbumIds"], scope, "unknownRoot", failures);
        JsonElement albumNode = root.GetProperty("albums").EnumerateArray().First(item => item.GetProperty("id").GetString() == albumId);
        AssertUnknownObject(albumNode, expected.GetProperty("unknownAlbum"), ["id", "name", "pinned", "coverMemberId", "createdAtUtc", "updatedAtUtc", "revision", "members"], scope, "unknownAlbum", failures);
        JsonElement unknownMemberExpected = expected.GetProperty("unknownMember");
        string memberId = unknownMemberExpected.GetProperty("memberId").GetString()!;
        JsonElement memberNode = albumNode.GetProperty("members").EnumerateArray().First(item => item.GetProperty("id").GetString() == memberId);
        AssertUnknownObject(memberNode, unknownMemberExpected.GetProperty("fields"), ["id", "imagePath", "addedAtUtc"], scope, "unknownMember", failures);
    }

    private static void PrepareInitial(string storePath, JsonElement initial, string caseRoot)
    {
        string mode = initial.GetProperty("mode").GetString()!;
        if (mode == "missing")
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        File.WriteAllBytes(storePath, BuildFixtureBytes(initial, caseRoot));
    }

    private static byte[] BuildFixtureBytes(JsonElement descriptor, string caseRoot)
    {
        string mode = descriptor.GetProperty("mode").GetString()!;
        if (mode == "raw")
            return Utf8WithoutBom.GetBytes(descriptor.GetProperty("text").GetString()!);
        if (mode == "bytes-base64")
            return Convert.FromBase64String(descriptor.GetProperty("base64").GetString()!);
        if (mode == "generated-utf8")
        {
            int byteLength = descriptor.GetProperty("byteLength").GetInt32();
            byte[] prefix = Utf8WithoutBom.GetBytes(descriptor.GetProperty("prefix").GetString()!);
            byte[] suffix = Utf8WithoutBom.GetBytes(descriptor.GetProperty("suffix").GetString()!);
            byte fill = checked((byte)descriptor.GetProperty("fillByte").GetInt32());
            byte[] generated = new byte[byteLength];
            prefix.CopyTo(generated, 0);
            generated.AsSpan(prefix.Length, byteLength - prefix.Length - suffix.Length).Fill(fill);
            suffix.CopyTo(generated, byteLength - suffix.Length);
            return generated;
        }
        if (mode != "json")
            throw new InvalidDataException($"unsupported fixture mode {mode}");
        JsonNode? node = JsonNode.Parse(descriptor.GetProperty("document").GetRawText());
        ExpandPlaceholders(node, caseRoot);
        return Utf8WithoutBom.GetBytes(
            node!.ToJsonString(IndentedJson) + Environment.NewLine);
    }

    private static byte[] CanonicalizeContractBytes(byte[] bytes)
    {
        string text = StrictUtf8WithoutBom.GetString(bytes);
        string withoutCrlf = text.Replace("\r\n", string.Empty, StringComparison.Ordinal);
        if (withoutCrlf.Contains('\r'))
            throw new InvalidDataException("Parity contract contains a bare carriage return");
        return Utf8WithoutBom.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void ExpandPlaceholders(JsonNode? node, string caseRoot)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(static pair => pair.Key).ToArray())
            {
                JsonNode? child = obj[key];
                if (child is JsonValue value && value.TryGetValue(out string? text) && text is not null)
                    obj[key] = ExpandString(text, caseRoot);
                else
                    ExpandPlaceholders(child, caseRoot);
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                JsonNode? child = array[index];
                if (child is JsonValue value && value.TryGetValue(out string? text) && text is not null)
                    array[index] = ExpandString(text, caseRoot);
                else
                    ExpandPlaceholders(child, caseRoot);
            }
        }
    }

    private static string ExpandString(string value, string caseRoot)
    {
        bool hadRootPlaceholder = value.Contains("${ROOT}", StringComparison.Ordinal);
        string expanded = value.Replace("${ROOT}", caseRoot, StringComparison.Ordinal);
        if (expanded.Contains("${", StringComparison.Ordinal))
            throw new InvalidDataException("unknown contract placeholder");
        if (hadRootPlaceholder)
        {
            string fullPath = Path.GetFullPath(expanded);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(caseRoot));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("contract path placeholder escaped its case temp root");
            }
            return fullPath;
        }
        return expanded;
    }

    private static IReadOnlyList<string> ReadExpandedPaths(JsonElement paths, string caseRoot)
        => paths.EnumerateArray()
            .Select(item => Path.GetFullPath(ExpandString(item.GetString()!, caseRoot)))
            .ToArray();

    private static void AssertStatuses(IReadOnlyList<string> actual, JsonElement expected, string scope, List<string> failures)
    {
        if (expected.ValueKind == JsonValueKind.Array)
        {
            AssertStringArray(actual, expected, scope, "statuses", failures);
            return;
        }
        string expectedStatus = expected.GetProperty("all").GetString()!;
        int expectedCount = expected.GetProperty("count").GetInt32();
        Expect(failures, scope, actual.Count == expectedCount, $"status count expected {expectedCount}, got {actual.Count}");
        for (int index = 0; index < actual.Count; index++)
            Expect(failures, scope, actual[index] == expectedStatus, $"status {index} expected {expectedStatus}, got {actual[index]}");
    }

    private static void AssertStringArray(IReadOnlyList<string> actual, JsonElement expected, string scope, string label, List<string> failures)
        => ExpectSequence(failures, scope, actual, expected.EnumerateArray().Select(static item => item.GetString()!).ToArray(), label);

    private static void AssertBooleanArray(IReadOnlyList<bool> actual, JsonElement expected, string scope, string label, List<string> failures)
        => ExpectSequence(failures, scope, actual, expected.EnumerateArray().Select(static item => item.GetBoolean()).ToArray(), label);

    private static void AssertNullableLongArray(IReadOnlyList<long?> actual, JsonElement expected, string scope, string label, List<string> failures)
        => ExpectSequence(failures, scope, actual, expected.EnumerateArray().Select(static item => (long?)item.GetInt64()).ToArray(), label);

    private static void ExpectSequence<T>(List<string> failures, string scope, IReadOnlyList<T> actual, IReadOnlyList<T> expected, string label)
    {
        if (actual.Count != expected.Count)
        {
            failures.Add($"{scope}: {label} count expected {expected.Count}, got {actual.Count}");
            return;
        }
        for (int index = 0; index < actual.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(actual[index], expected[index]))
                failures.Add($"{scope}: {label}[{index}] mismatch");
        }
    }

    private static void ExpectPathSequence(List<string> failures, string scope, IReadOnlyList<string> actual, IReadOnlyList<string> expected, string label)
    {
        if (actual.Count != expected.Count)
        {
            failures.Add($"{scope}: {label} count expected {expected.Count}, got {actual.Count}");
            return;
        }
        for (int index = 0; index < actual.Count; index++)
        {
            if (!string.Equals(Path.GetFullPath(actual[index]), Path.GetFullPath(expected[index]), StringComparison.OrdinalIgnoreCase))
                failures.Add($"{scope}: {label}[{index}] mismatch");
        }
    }

    private static void AssertNullableLong(long? actual, JsonElement expected, string scope, string label, List<string> failures)
    {
        long? expectedValue = expected.ValueKind == JsonValueKind.Null ? null : expected.GetInt64();
        Expect(failures, scope, actual == expectedValue, $"{label} expected {expectedValue?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
    }

    private static void AssertNullableInt(int? actual, JsonElement expected, string scope, string label, List<string> failures)
    {
        int? expectedValue = expected.ValueKind == JsonValueKind.Null ? null : expected.GetInt32();
        Expect(failures, scope, actual == expectedValue, $"{label} expected {expectedValue?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
    }

    private static void AssertUnknownRoot(
        string path,
        JsonElement expected,
        IReadOnlyCollection<string> known,
        bool inspectSupportedDocument,
        string scope,
        List<string> failures)
    {
        if (!inspectSupportedDocument || !File.Exists(path))
        {
            using JsonDocument empty = JsonDocument.Parse("{}");
            Expect(failures, scope, JsonEquivalent(empty.RootElement, expected), "unknownRoot mismatch");
            return;
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        AssertUnknownObject(document.RootElement, expected, known, scope, "unknownRoot", failures);
    }

    private static void AssertExpectedFinalDocument(
        string path,
        JsonElement expectedFinal,
        string caseRoot,
        string scope,
        List<string> failures)
    {
        string mode = expectedFinal.GetProperty("mode").GetString()!;
        if (mode == "missing")
        {
            Expect(failures, scope, !File.Exists(path), "expected final document to be missing");
            return;
        }
        if (!File.Exists(path))
        {
            failures.Add($"{scope}: expected final document is missing");
            return;
        }
        if (mode == "raw")
        {
            Expect(
                failures,
                scope,
                string.Equals(
                    File.ReadAllText(path),
                    expectedFinal.GetProperty("text").GetString(),
                    StringComparison.Ordinal),
                "final raw bytes mismatch");
            return;
        }
        if (mode is "bytes-base64" or "generated-utf8")
        {
            Expect(
                failures,
                scope,
                File.ReadAllBytes(path).AsSpan().SequenceEqual(
                    BuildFixtureBytes(expectedFinal, caseRoot)),
                "final exact bytes mismatch");
            return;
        }

        using JsonDocument actual = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement expandedExpected = ExpandJsonElement(
            expectedFinal.GetProperty("document"),
            caseRoot);
        Expect(
            failures,
            scope,
            JsonEquivalent(actual.RootElement, expandedExpected),
            "final JSON document mismatch");
    }

    private static JsonElement ExpandJsonElement(JsonElement element, string caseRoot)
    {
        JsonNode node = JsonNode.Parse(element.GetRawText())
            ?? throw new InvalidDataException("expected JSON document was null");
        ExpandPlaceholders(node, caseRoot);
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void AssertUnknownObject(
        JsonElement actualObject,
        JsonElement expected,
        IReadOnlyCollection<string> known,
        string scope,
        string label,
        List<string> failures)
    {
        var unknown = new JsonObject();
        foreach (JsonProperty property in actualObject.EnumerateObject())
        {
            if (!known.Contains(property.Name))
                unknown[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        using JsonDocument actual = JsonDocument.Parse(unknown.ToJsonString());
        Expect(failures, scope, JsonEquivalent(actual.RootElement, expected), $"{label} mismatch");
    }

    private static bool JsonEquivalent(JsonElement left, JsonElement right)
        => string.Equals(CanonicalJson(left), CanonicalJson(right), StringComparison.Ordinal);

    private static string CanonicalJson(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .Select(static property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => throw new InvalidDataException("unsupported JSON value kind"),
        };

    private static byte[]? ReadOptionalBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static bool OptionalBytesEqual(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static long? OptionalLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement value) ? value.GetInt64() : null;

    private static void Expect(List<string> failures, string scope, bool condition, string message)
    {
        if (!condition)
            failures.Add($"{scope}: {message}");
    }

    private static string RequirePath(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{label} is required");
        return Path.GetFullPath(value);
    }

    private static string ValidateTempPath(string? value, string label, bool requireNested)
    {
        string fullPath = RequirePath(value, label);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string prefix = tempRoot + Path.DirectorySeparatorChar;
        bool nested = fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        if (!nested || (requireNested && string.Equals(fullPath, tempRoot, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{label} must be nested under the operating-system temp directory");
        return fullPath;
    }

    private static void WriteReceipt(string receiptPath, ParityContractReceipt receipt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, IndentedJson) + Environment.NewLine, Utf8WithoutBom);
    }

    private static string OneLine(string message)
        => string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void RequireObject(JsonElement value, string scope)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{scope} must be an object");
    }

    private static JsonElement RequireArray(JsonElement obj, string name, string scope, bool requireNonEmpty = false)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{scope}.{name} must be an array");
        if (requireNonEmpty && value.GetArrayLength() == 0)
            throw new InvalidDataException($"{scope}.{name} must not be empty");
        return value;
    }

    private static void RequireArray(JsonElement value, string scope, bool requireNonEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.Array || (requireNonEmpty && value.GetArrayLength() == 0))
            throw new InvalidDataException($"{scope} must be {(requireNonEmpty ? "a non-empty" : "an")} array");
    }

    private static string RequireString(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{scope}.{name} must be a string");
        return value.GetString()!;
    }

    private static void RequireBoolean(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{scope}.{name} must be a boolean");
        }
    }

    private static void RequireNullableString(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            throw new InvalidDataException($"{scope}.{name} must be a string or null");
        }
    }

    private static int RequireNonNegativeInt(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result) || result < 0)
            throw new InvalidDataException($"{scope}.{name} must be a non-negative integer");
        return result;
    }

    private static long RequireNonNegativeLong(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value) || !value.TryGetInt64(out long result) || result < 0)
            throw new InvalidDataException($"{scope}.{name} must be a non-negative integer");
        return result;
    }

    private static void RequireNullableNonNegativeInt(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || (value.ValueKind != JsonValueKind.Null && (!value.TryGetInt32(out int result) || result < 0)))
        {
            throw new InvalidDataException($"{scope}.{name} must be a non-negative integer or null");
        }
    }

    private static void RequireNullableNonNegativeLong(JsonElement obj, string name, string scope)
    {
        if (!obj.TryGetProperty(name, out JsonElement value)
            || (value.ValueKind != JsonValueKind.Null && (!value.TryGetInt64(out long result) || result < 0)))
        {
            throw new InvalidDataException($"{scope}.{name} must be a non-negative integer or null");
        }
    }

    private static void RequireProperties(
        JsonElement obj,
        string scope,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{scope} has unknown property {property.Name}");
        }
        foreach (string name in required)
        {
            if (!obj.TryGetProperty(name, out _))
                throw new InvalidDataException($"{scope} is missing {name}");
        }
    }

    private static void ValidateStringArray(JsonElement array, string scope)
    {
        RequireArray(array, scope);
        if (array.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String))
            throw new InvalidDataException($"{scope} must contain only strings");
    }

    private static void ValidateBooleanArray(JsonElement array, string scope)
    {
        RequireArray(array, scope);
        if (array.EnumerateArray().Any(static value => value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidDataException($"{scope} must contain only booleans");
    }

    private static void ValidateLongArray(JsonElement array, string scope)
    {
        RequireArray(array, scope);
        if (array.EnumerateArray().Any(static value => !value.TryGetInt64(out long number) || number < 0))
            throw new InvalidDataException($"{scope} must contain only non-negative integers");
    }

    private sealed record ContractVector(string Id, string Kind, IReadOnlyList<JsonElement> Cases);

    private sealed class ParityContractReceipt
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("runtime")]
        public string RuntimeName { get; init; } = Runtime;

        [JsonPropertyName("contractSha256")]
        public string ContractSha256 { get; set; } = "";

        [JsonPropertyName("contractIds")]
        public List<string> ContractIds { get; } = [];

        [JsonPropertyName("caseIds")]
        public List<string> CaseIds { get; } = [];

        [JsonPropertyName("casesRun")]
        public int CasesRun { get; set; }

        [JsonPropertyName("failures")]
        public List<string> Failures { get; } = [];
    }
}
