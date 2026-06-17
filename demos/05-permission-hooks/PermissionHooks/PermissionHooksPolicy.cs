using System.Text.Json;

namespace CopilotSDK.Demos.PermissionHooks;

public static class PermissionHooksPolicy
{
    /// Nazwy te reprezentują typowe narzędzia SDK/runtime do zapisu plików. Środowisko
    /// wykonawcze może kwalifikować nazwy narzędzi przestrzenią nazw, więc późniejsze
    /// dopasowanie normalizuje przyrostki, zamiast wymagać dokładnych ciągów znaków.
    private static readonly string[] KnownFileWriteTools =
    [
        "write_file",
        "edit_file",
        "create_file",
    ];

    /// Dostęp do powłoki wiąże się z wysokim ryzykiem, ponieważ może uruchamiać dowolne
    /// polecenia. Polityka demo odrzuca tego rodzaju narzędzia zarówno na poziomie żądania
    /// uprawnień, jak i PreToolUse, aby pokazać obronę w głąb wokół wykonywania narzędzi SDK.
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

    public static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));

        return EnsureTrailingSeparator(Path.GetFullPath(workspaceRoot));
    }

    public static bool IsPermissionKindAllowed(string kind)
    {
        // Ta zgrubna brama jest używana przez OnPermissionRequest, zanim sprawdzane są
        // konkretne argumenty narzędzia. Dopuszcza tylko znane bezpieczne kategorie;
        // późniejszy hook PreToolUse wykonuje walidację na poziomie argumentów.
        return ToolMatches(kind, "list_csharp_files") || IsKnownFileWriteTool(kind);
    }

    public static PermissionHooksPolicyDecision EvaluatePreToolUse(
        string toolName,
        string? toolArgs,
        string workspaceRoot)
    {
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);

        if (IsShellTool(toolName))
            return PermissionHooksPolicyDecision.Deny("Shell tools are blocked by this demo policy.");

        if (ToolMatches(toolName, "list_csharp_files"))
            // Niestandardowe narzędzie SDK ignoruje ścieżki dostarczone przez model i zawsze
            // listuje pliki ze znormalizowanego katalogu głównego obszaru roboczego, więc można bezpiecznie zezwolić.
            return PermissionHooksPolicyDecision.Allow("C# file listing is constrained to the workspace root.");

        if (!IsKnownFileWriteTool(toolName))
            return PermissionHooksPolicyDecision.Deny("Unknown tools are denied by default.");

        var paths = ExtractPathCandidates(toolArgs).ToArray();
        if (paths.Length == 0)
            return PermissionHooksPolicyDecision.Deny("File write tools must include an explicit path.");

        // PreToolUse otrzymuje argumenty JSON wygenerowane przez model. Polityka
        // traktuje każdy argument przypominający ścieżkę jako potencjalny cel efektu ubocznego i
        // sprawdza je wszystkie przed zezwoleniem SDK na uruchomienie narzędzia.
        foreach (var path in paths)
        {
            if (!TryResolveSafeWorkspacePath(normalizedRoot, path, out var fullPath, out var reason))
                return PermissionHooksPolicyDecision.Deny(reason);

            if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
                return PermissionHooksPolicyDecision.Deny("Only .cs files can be written by this demo policy.");
        }

        return PermissionHooksPolicyDecision.Allow("Allowed .cs write inside the workspace root.");
    }

    public static string ListCSharpFiles(string workspaceRoot, int maxFiles = 10)
    {
        // Ta metoda jest udostępniana jako AIFunction w Program.cs. Ogranicza
        // odnajdywanie plików C# do zatwierdzonego katalogu głównego, aby dostarczyć
        // modelowi użyteczny kontekst bez szerokiego dostępu do systemu plików.
        var normalizedRoot = NormalizeWorkspaceRoot(workspaceRoot);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = Directory.EnumerateFiles(normalizedRoot, "*.cs", options)
            .Where(path => TryResolveSafeWorkspacePath(normalizedRoot, Path.GetRelativePath(normalizedRoot, path), out _, out _))
            .Take(maxFiles)
            .Select(path => Path.GetRelativePath(normalizedRoot, path))
            .ToList();

        return string.Join("\n", files);
    }

    private static bool IsKnownFileWriteTool(string toolName) =>
        ToolMatches(toolName, KnownFileWriteTools);

    private static bool IsShellTool(string toolName) =>
        ToolMatches(toolName, ShellTools);

    private static IEnumerable<string> ExtractPathCandidates(string? toolArgs)
    {
        // ToolArgs pochodzi z SDK jako serializowany JSON wygenerowany przez model.
        // Nieprawidłowy JSON jest traktowany jako „brak użytecznej ścieżki”, co powoduje
        // odmowę zapisu zamiast zgadywania.
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
                property.Value.GetString() is { Length: > 0 } value)
            {
                yield return value;
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

    private static bool IsPathProperty(string propertyName)
    {
        return propertyName.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Contains("file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveSafeWorkspacePath(
        string normalizedRoot,
        string path,
        out string fullPath,
        out string reason)
    {
        // SDK deleguje autoryzację do hosta, więc bezpieczeństwo ścieżki musi być
        // egzekwowane tutaj, zanim narzędzie dotknie systemu plików.
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Empty paths are not allowed.";
            return false;
        }

        if (path.StartsWith('~') || Path.IsPathFullyQualified(path) || IsUncPath(path))
        {
            reason = "Absolute, home-relative, and network paths are not allowed.";
            return false;
        }

        fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, path));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Path traversal outside the workspace root is blocked.";
            return false;
        }

        if (ContainsReparsePoint(normalizedRoot, fullPath))
        {
            reason = "Paths that traverse symlinks or reparse points are not allowed.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool ContainsReparsePoint(string normalizedRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(normalizedRoot, fullPath);
        var current = normalizedRoot;

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

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool ToolMatches(string toolName, params string[] names)
    {
        var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
        return names.Any(name =>
            normalized == name ||
            normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($":{name}", StringComparison.Ordinal));
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

public sealed record PermissionHooksPolicyDecision(bool Allowed, string Reason)
{
    public static PermissionHooksPolicyDecision Allow(string reason) => new(true, reason);

    public static PermissionHooksPolicyDecision Deny(string reason) => new(false, reason);
}
