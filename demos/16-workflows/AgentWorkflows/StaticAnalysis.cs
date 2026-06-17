using System.Text.RegularExpressions;

namespace CopilotSDK.Demos.Demos.AgentWorkflows;

internal static class StaticAnalysis
{
    private static readonly Regex TodoRegex = new(@"\b(TODO|FIXME|HACK)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretRegex = new(@"(api[_-]?key|password|token)\s*[:=]\s*[""']?[A-Za-z0-9_\-]{8,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static StaticAnalysisReport Analyze(string projectRoot)
    {
        var files = Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(path))
            .ToArray();

        var totalLines = 0;
        var todoHits = new List<string>();
        var secretHits = new List<string>();
        var namingViolations = new List<string>();
        var packageReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            var extension = Path.GetExtension(file);

            if (extension is ".cs" or ".md" or ".json" or ".csproj")
            {
                var lines = File.ReadAllLines(file);
                totalLines += lines.Count(line => !string.IsNullOrWhiteSpace(line));

                if (lines.Any(line => TodoRegex.IsMatch(line)))
                    todoHits.Add(relative);

                if (lines.Any(line => SecretRegex.IsMatch(line)))
                    secretHits.Add(relative);

                if (extension == ".csproj")
                {
                    foreach (var line in lines)
                    {
                        var marker = "PackageReference Include=\"";
                        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (index < 0)
                            continue;

                        var start = index + marker.Length;
                        var end = line.IndexOf('"', start);
                        if (end > start)
                            packageReferences.Add(line[start..end]);
                    }
                }
            }

            if (extension == ".cs")
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!char.IsUpper(fileName[0]) || fileName.Contains('_') || fileName.Contains('-'))
                    namingViolations.Add(relative);
            }
        }

        return new StaticAnalysisReport(
            TotalFiles: files.Length,
            NonEmptyLines: totalLines,
            TodoFiles: todoHits.OrderBy(x => x).ToArray(),
            PotentialSecretFiles: secretHits.OrderBy(x => x).ToArray(),
            NamingViolations: namingViolations.OrderBy(x => x).ToArray(),
            PackageReferences: packageReferences.OrderBy(x => x).ToArray());
    }

    private static bool IsIgnored(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record StaticAnalysisReport(
    int TotalFiles,
    int NonEmptyLines,
    IReadOnlyList<string> TodoFiles,
    IReadOnlyList<string> PotentialSecretFiles,
    IReadOnlyList<string> NamingViolations,
    IReadOnlyList<string> PackageReferences)
{
    public string ToMarkdown()
    {
        var lines = new List<string>
        {
            "## Deterministic static analysis",
            $"- Total files scanned: {TotalFiles}",
            $"- Non-empty lines: {NonEmptyLines}",
            $"- Files with TODO/FIXME/HACK: {TodoFiles.Count}",
            $"- Potential secret patterns: {PotentialSecretFiles.Count}",
            $"- Naming violations (.cs): {NamingViolations.Count}",
            $"- Package references: {(PackageReferences.Count == 0 ? "none" : string.Join(", ", PackageReferences))}"
        };

        if (TodoFiles.Count > 0)
            lines.Add($"- TODO files: {string.Join(", ", TodoFiles)}");

        if (PotentialSecretFiles.Count > 0)
            lines.Add($"- Potential secret files: {string.Join(", ", PotentialSecretFiles)}");

        if (NamingViolations.Count > 0)
            lines.Add($"- Naming violations: {string.Join(", ", NamingViolations)}");

        return string.Join(Environment.NewLine, lines);
    }
}
