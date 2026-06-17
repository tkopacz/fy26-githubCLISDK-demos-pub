using System.Xml.Linq;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.GuardedCopilotCli;

public sealed class GuardedCopilotCliProjectTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string GuardedProjectFileName = "04 GuardedCopilotCli.csproj";
    private static readonly string ProjectDirectory = Path.Combine(
        RepositoryRoot,
        "demos",
        "20-guarded-cli",
        "GuardedCopilotCli");

    [Fact]
    public void Solution_IncludesGuardedCopilotCliProject()
    {
        var solutionPath = Path.Combine(RepositoryRoot, "CopilotSDK.Demos.slnx");
        var solution = File.ReadAllText(solutionPath);

        solution.Should().Contain($"demos/20-guarded-cli/GuardedCopilotCli/{GuardedProjectFileName}");
    }

    [Fact]
    public void ProjectFile_PublishesAgentsAndSampleWorkspace()
    {
        var project = XDocument.Load(Path.Combine(ProjectDirectory, GuardedProjectFileName));
        var contentItems = project.Descendants("Content").ToArray();

        contentItems.Where(element =>
            (string?)element.Attribute("Include") == @".github\agents\**\*.md" &&
            element.Element("CopyToPublishDirectory") is { Value: "PreserveNewest" } &&
            element.Element("TargetPath") is { Value: @".github\agents\%(RecursiveDir)%(Filename)%(Extension)" })
            .Should()
            .ContainSingle();

        contentItems.Where(element =>
            (string?)element.Attribute("Include") == @"SampleWorkspace\**\*.*" &&
            element.Element("CopyToPublishDirectory") is { Value: "PreserveNewest" })
            .Should()
            .ContainSingle();
    }

    [Fact]
    public void ProjectFile_PublishSingleFileExtractsBundledRuntime()
    {
        var project = XDocument.Load(Path.Combine(ProjectDirectory, GuardedProjectFileName));

        project.Descendants("IncludeAllContentForSelfExtract")
            .Should()
            .ContainSingle(element =>
                element.Value == "true" &&
                (string?)element.Attribute("Condition") == "'$(PublishSingleFile)' == 'true'");
    }

    [Fact]
    public void LocalAgentDefinitions_UseOnlyGuardedWorkspaceTools()
    {
        var allowedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "list_workspace_files",
            "read_workspace_file",
            "write_workspace_file",
            "search_workspace_text",
        };
        var agentsDirectory = Path.Combine(ProjectDirectory, ".github", "agents");

        foreach (var agentPath in Directory.EnumerateFiles(agentsDirectory, "*.md"))
        {
            var frontmatter = ParseFrontmatter(File.ReadAllLines(agentPath));
            var tools = ParseTools(frontmatter["tools"]);

            tools.Should()
                .OnlyContain(tool => allowedTools.Contains(tool), $"{Path.GetFileName(agentPath)} must not enable shell, terminal, or generic agent delegation tools");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotSDK.Demos.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing CopilotSDK.Demos.slnx.");
    }

    private static Dictionary<string, string> ParseFrontmatter(string[] lines)
    {
        lines.Should().NotBeEmpty();
        lines[0].Should().Be("---");

        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim() == "---")
                return frontmatter;

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex > 0)
                frontmatter[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        throw new InvalidDataException("Agent frontmatter is not closed.");
    }

    private static IReadOnlyList<string> ParseTools(string value) =>
        value.Trim()
            .TrimStart('[')
            .TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tool => tool.Trim().Trim('"', '\''))
            .ToArray();
}
