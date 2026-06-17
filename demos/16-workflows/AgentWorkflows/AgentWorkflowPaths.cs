namespace CopilotSDK.Demos.Demos.AgentWorkflows;

public static class AgentWorkflowPaths
{
    public static string ResolveSampleProjectRoot(string baseDirectory, IReadOnlyList<string> args)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Base directory is required.", nameof(baseDirectory));

        var projectRoot = GetProjectRootOverride(args)
            ?? Path.Combine(baseDirectory, "SampleProject");
        projectRoot = Path.GetFullPath(projectRoot);

        ValidateSampleProjectRoot(projectRoot);
        return projectRoot;
    }

    public static void ValidateSampleProjectRoot(string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
            throw new DirectoryNotFoundException($"Sample project directory not found: {projectRoot}");

        var agentsDirectory = Path.Combine(projectRoot, ".github", "agents");
        if (!Directory.Exists(agentsDirectory) ||
            !Directory.EnumerateFiles(agentsDirectory, "*.md").Any())
            throw new DirectoryNotFoundException($"Sample project custom agents not found: {agentsDirectory}");

        var sourceDirectory = Path.Combine(projectRoot, "src");
        if (!Directory.Exists(sourceDirectory) ||
            !Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories).Any())
            throw new FileNotFoundException($"Sample project C# source files not found under: {sourceDirectory}");

        if (!Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            throw new FileNotFoundException($"Sample project .csproj file not found: {projectRoot}");
    }

    private static string? GetProjectRootOverride(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--project-root=", StringComparison.OrdinalIgnoreCase))
                return arg["--project-root=".Length..];

            if (string.Equals(arg, "--project-root", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Count)
                return args[i + 1];
        }

        return args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal));
    }
}
