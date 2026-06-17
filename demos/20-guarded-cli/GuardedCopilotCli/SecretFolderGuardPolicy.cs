using System.Text.Json;

namespace CopilotSDK.Demos.GuardedCopilotCli;

internal static class SecretFolderGuardPolicy
{
    private const string ProtectedFolderName = "TAJNE";

    // Te nazwy muszą odpowiadać wartościom AIFunctionFactoryOptions.Name
    // zarejestrowanym w Program.cs. PermissionRequest.Kind i ToolName z hooka
    // mogą zawierać prefiksy runtime'u/serwera, więc niżej dopasowujemy po sufiksie.
    private static readonly string[] KnownTools =
    [
        "list_workspace_files",
        "read_workspace_file",
        "write_workspace_file",
        "search_workspace_text",
    ];

    private static readonly string[] MetadataTools =
    [
        "report_intent",
    ];

    private static readonly string[] ShellTools =
    [
        "bash",
        "cmd",
        "powershell",
        "run_command",
        "run_in_terminal",
        "run_shell_command",
        "shell",
        "terminal",
    ];

    internal static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        return EnsureTrailingSeparator(Path.GetFullPath(workspaceRoot));
    }

    internal static bool IsPermissionKindAllowed(string kind) =>
        ToolMatches(kind, KnownTools) || ToolMatches(kind, MetadataTools);

    // Ta metoda jest wywoływana z SessionHooks.OnPreToolUse. W tym momencie
    // Copilot SDK pozwolił już modelowi wybrać narzędzie i przygotować argumenty
    // JSON, ale funkcja hosta jeszcze nie została uruchomiona.
    internal static SecretFolderGuardDecision EvaluatePreToolUse(
        string toolName,
        string? toolArgs,
        string workspaceRoot)
    {
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);

        if (IsShellTool(toolName))
            return SecretFolderGuardDecision.Deny("Shell tools are blocked by host policy.");

        if (ToolMatches(toolName, MetadataTools))
            return SecretFolderGuardDecision.Allow("Metadata-only SDK tool does not access workspace files.");

        if (!ToolMatches(toolName, KnownTools))
            return SecretFolderGuardDecision.Deny("Unknown tools are denied by host policy.");

        var paths = ExtractPathCandidates(toolArgs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ToolMatches(toolName, "list_workspace_files", "search_workspace_text"))
        {
            if (paths.Length == 0)
                return SecretFolderGuardDecision.Allow("No protected path in arguments.");

            foreach (var path in paths)
            {
                var resolution = ResolvePath(normalizedRoot, path);
                if (!resolution.Allowed)
                    return SecretFolderGuardDecision.Deny(resolution.Reason);
            }

            return SecretFolderGuardDecision.Allow("Directory argument is inside allowed workspace.");
        }

        if (paths.Length == 0)
            return SecretFolderGuardDecision.Deny("File operations require an explicit path.");

        foreach (var path in paths)
        {
            var resolution = ResolvePath(normalizedRoot, path);
            if (!resolution.Allowed)
                return SecretFolderGuardDecision.Deny(resolution.Reason);
        }

        return SecretFolderGuardDecision.Allow("Path is inside allowed workspace.");
    }

    internal static WorkspaceListResult ListWorkspaceFiles(
        string workspaceRoot,
        string relativeDirectory,
        int maxResults)
    {
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var requestedDirectory = string.IsNullOrWhiteSpace(relativeDirectory) ? "." : relativeDirectory;
        var resolution = ResolvePath(normalizedRoot, requestedDirectory);
        if (!resolution.Allowed)
            return WorkspaceListResult.CreateBlocked(requestedDirectory, resolution.Reason);

        if (!Directory.Exists(resolution.FullPath))
            return WorkspaceListResult.CreateBlocked(requestedDirectory, "Requested directory does not exist.");

        var files = new List<string>();
        var blockedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedReparsePoints = 0;
        var truncated = false;
        var effectiveMaxResults = Math.Clamp(maxResults, 1, 200);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(resolution.FullPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            IEnumerable<string> childDirectories;
            try
            {
                childDirectories = Directory.EnumerateDirectories(currentDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                blockedDirectories.Add(ToDisplayRelative(normalizedRoot, currentDirectory) + " [inaccessible]");
                continue;
            }
            catch (IOException)
            {
                blockedDirectories.Add(ToDisplayRelative(normalizedRoot, currentDirectory) + " [io-error]");
                continue;
            }

            foreach (var childDirectory in childDirectories)
            {
                var relativeChildDirectory = ToDisplayRelative(normalizedRoot, childDirectory);
                if (ContainsProtectedSegment(relativeChildDirectory))
                {
                    blockedDirectories.Add(relativeChildDirectory);
                    continue;
                }

                if (HasReparsePoint(childDirectory))
                {
                    blockedDirectories.Add(relativeChildDirectory + " [reparse-point]");
                    skippedReparsePoints++;
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }

            IEnumerable<string> childFiles;
            try
            {
                childFiles = Directory.EnumerateFiles(currentDirectory);
            }
            catch (UnauthorizedAccessException)
            {
                blockedDirectories.Add(ToDisplayRelative(normalizedRoot, currentDirectory) + " [inaccessible]");
                continue;
            }
            catch (IOException)
            {
                blockedDirectories.Add(ToDisplayRelative(normalizedRoot, currentDirectory) + " [io-error]");
                continue;
            }

            foreach (var childFile in childFiles)
            {
                var relativeFile = ToDisplayRelative(normalizedRoot, childFile);
                if (ContainsProtectedSegment(relativeFile))
                    continue;

                files.Add(relativeFile);
                if (files.Count >= effectiveMaxResults)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
                break;
        }

        return new WorkspaceListResult(
            Blocked: false,
            Reason: "ok",
            RequestedDirectory: string.IsNullOrWhiteSpace(resolution.RelativePath) ? "." : resolution.RelativePath,
            Files: files,
            BlockedDirectories: blockedDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            SkippedReparsePoints: skippedReparsePoints,
            Truncated: truncated);
    }

    internal static WorkspaceReadResult ReadWorkspaceFile(
        string workspaceRoot,
        string path,
        int maxCharacters = 4000)
    {
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var resolution = ResolvePath(normalizedRoot, path);
        if (!resolution.Allowed)
            return WorkspaceReadResult.CreateBlocked(path, resolution.Reason);

        if (Directory.Exists(resolution.FullPath))
            return WorkspaceReadResult.CreateBlocked(path, "Requested path is a directory, not a file.");

        if (!File.Exists(resolution.FullPath))
            return WorkspaceReadResult.CreateBlocked(path, "Requested file does not exist.");

        string content;
        try
        {
            content = File.ReadAllText(resolution.FullPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WorkspaceReadResult.CreateBlocked(path, ex.Message);
        }
        catch (IOException ex)
        {
            return WorkspaceReadResult.CreateBlocked(path, ex.Message);
        }

        var truncated = false;
        if (content.Length > maxCharacters)
        {
            content = content[..maxCharacters] + "\n[content truncated]";
            truncated = true;
        }

        return new WorkspaceReadResult(
            Blocked: false,
            Reason: "ok",
            Path: resolution.RelativePath,
            Content: content,
            Truncated: truncated);
    }

    internal static WorkspaceWriteResult WriteWorkspaceFile(
        string workspaceRoot,
        string path,
        string content,
        bool overwrite)
    {
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var resolution = ResolvePath(normalizedRoot, path);
        if (!resolution.Allowed)
            return WorkspaceWriteResult.CreateBlocked(path, resolution.Reason);

        if (Directory.Exists(resolution.FullPath))
            return WorkspaceWriteResult.CreateBlocked(path, "Requested path is a directory, not a writable file.");

        if (File.Exists(resolution.FullPath) && !overwrite)
            return WorkspaceWriteResult.CreateBlocked(path, "File already exists. Set overwrite=true to replace it.");

        var parentDirectory = Path.GetDirectoryName(resolution.FullPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        try
        {
            File.WriteAllText(resolution.FullPath, content);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WorkspaceWriteResult.CreateBlocked(path, ex.Message);
        }
        catch (IOException ex)
        {
            return WorkspaceWriteResult.CreateBlocked(path, ex.Message);
        }

        return new WorkspaceWriteResult(
            Blocked: false,
            Reason: "ok",
            Path: resolution.RelativePath,
            WroteFile: true);
    }

    internal static WorkspaceSearchResult SearchWorkspaceText(
        string workspaceRoot,
        string query,
        string relativeDirectory,
        int maxResults)
    {
        if (string.IsNullOrWhiteSpace(query))
            return WorkspaceSearchResult.CreateBlocked(query, relativeDirectory, "Search query cannot be empty.");

        var effectiveMaxResults = Math.Clamp(maxResults, 1, 100);
        var listing = ListWorkspaceFiles(workspaceRoot, relativeDirectory, Math.Max(effectiveMaxResults * 20, 200));
        if (listing.Blocked)
            return WorkspaceSearchResult.CreateBlocked(query, relativeDirectory, listing.Reason);

        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var hits = new List<WorkspaceSearchHit>(effectiveMaxResults);
        var truncated = listing.Truncated;

        foreach (var relativePath in listing.Files)
        {
            if (hits.Count >= effectiveMaxResults)
            {
                truncated = true;
                break;
            }

            var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!File.Exists(fullPath))
                continue;

            if (new FileInfo(fullPath).Length > 1_000_000)
                continue;

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(fullPath))
                {
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        continue;

                    hits.Add(new WorkspaceSearchHit(
                        Path: relativePath,
                        Line: lineNumber,
                        Snippet: line.Length > 180 ? line[..180] + "..." : line));

                    if (hits.Count >= effectiveMaxResults)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
        }

        return new WorkspaceSearchResult(
            Blocked: false,
            Reason: "ok",
            Query: query,
            RequestedDirectory: relativeDirectory,
            Hits: hits,
            BlockedDirectories: listing.BlockedDirectories,
            Truncated: truncated);
    }

    private static PathResolution ResolvePath(string normalizedRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PathResolution.Deny("Empty paths are not allowed.");

        if (path.StartsWith('~') || Path.IsPathFullyQualified(path) || IsUncPath(path))
            return PathResolution.Deny("Absolute, home-relative, and network paths are not allowed.");

        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, path));
        if (!IsSameOrChild(normalizedRoot, fullPath))
            return PathResolution.Deny("Path traversal outside the workspace root is blocked.");

        var relativePath = Path.GetRelativePath(normalizedRoot, fullPath);
        if (relativePath == ".")
            relativePath = string.Empty;

        if (ContainsProtectedSegment(relativePath))
            return PathResolution.Deny($"Access to '{ProtectedFolderName}' is blocked by host policy.");

        if (ContainsReparsePoint(normalizedRoot, fullPath))
            return PathResolution.Deny("Paths that traverse symlinks or reparse points are not allowed.");

        return PathResolution.Allow(fullPath, relativePath.Replace('\\', '/'));
    }

    private static bool ContainsProtectedSegment(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        return relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(ProtectedFolderName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsShellTool(string toolName) =>
        ToolMatches(toolName, ShellTools);

    private static IEnumerable<string> ExtractPathCandidates(string? toolArgs)
    {
        // ToolArgs to wygenerowany przez model JSON przekazany przez SDK. Blokada
        // traktuje każdą właściwość podobną do ścieżki jako istotną dla polityki,
        // bo model kontroluje zarówno nazwę narzędzia, jak i wartości argumentów.
        if (string.IsNullOrWhiteSpace(toolArgs))
            yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(toolArgs);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var value in ExtractPathCandidates(document.RootElement))
                yield return value;
        }
    }

    private static IEnumerable<string> ExtractPathCandidates(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                IsPathProperty(property.Name) &&
                property.Value.GetString() is { Length: > 0 } candidate)
            {
                yield return candidate;
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                foreach (var nested in ExtractNestedPathCandidates(property.Value))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<string> ExtractNestedPathCandidates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in ExtractPathCandidates(item))
                    yield return nested;
            }

            yield break;
        }

        foreach (var nested in ExtractPathCandidates(element))
            yield return nested;
    }

    private static bool IsPathProperty(string propertyName) =>
        propertyName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("file", StringComparison.OrdinalIgnoreCase) ||
        propertyName.Contains("directory", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrChild(string normalizedRoot, string fullPath)
    {
        var trimmedRoot = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmedRoot, fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return true;

        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReparsePoint(string normalizedRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(normalizedRoot, fullPath);
        if (relativePath == ".")
            return false;

        var current = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
        }

        return false;
    }

    private static bool HasReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string ToDisplayRelative(string normalizedRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        return (relative == "." ? "." : relative).Replace('\\', '/');
    }

    private static bool ToolMatches(string toolName, params string[] names)
    {
        var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
        return names.Any(name =>
            normalized == name ||
            normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($":{name}", StringComparison.Ordinal));
    }

    private sealed record PathResolution(bool Allowed, string Reason, string FullPath, string RelativePath)
    {
        public static PathResolution Allow(string fullPath, string relativePath) => new(true, "ok", fullPath, relativePath);

        public static PathResolution Deny(string reason) => new(false, reason, string.Empty, string.Empty);
    }
}

internal sealed record SecretFolderGuardDecision(bool Allowed, string Reason)
{
    public static SecretFolderGuardDecision Allow(string reason) => new(true, reason);

    public static SecretFolderGuardDecision Deny(string reason) => new(false, reason);
}

internal sealed record WorkspaceListResult(
    bool Blocked,
    string Reason,
    string RequestedDirectory,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> BlockedDirectories,
    int SkippedReparsePoints,
    bool Truncated)
{
    public static WorkspaceListResult CreateBlocked(string requestedDirectory, string reason) =>
        new(true, reason, requestedDirectory, [], [], 0, false);
}

internal sealed record WorkspaceReadResult(
    bool Blocked,
    string Reason,
    string Path,
    string? Content,
    bool Truncated)
{
    public static WorkspaceReadResult CreateBlocked(string path, string reason) =>
        new(true, reason, path, null, false);
}

internal sealed record WorkspaceWriteResult(
    bool Blocked,
    string Reason,
    string Path,
    bool WroteFile)
{
    public static WorkspaceWriteResult CreateBlocked(string path, string reason) =>
        new(true, reason, path, false);
}

internal sealed record WorkspaceSearchHit(
    string Path,
    int Line,
    string Snippet);

internal sealed record WorkspaceSearchResult(
    bool Blocked,
    string Reason,
    string Query,
    string RequestedDirectory,
    IReadOnlyList<WorkspaceSearchHit> Hits,
    IReadOnlyList<string> BlockedDirectories,
    bool Truncated)
{
    public static WorkspaceSearchResult CreateBlocked(string query, string requestedDirectory, string reason) =>
        new(true, reason, query, requestedDirectory, [], [], false);
}
