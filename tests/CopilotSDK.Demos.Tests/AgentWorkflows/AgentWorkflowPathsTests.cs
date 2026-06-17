using CopilotSDK.Demos.Demos.AgentWorkflows;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.AgentWorkflows;

public sealed class AgentWorkflowPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agent-workflows-").FullName;

    [Fact]
    public void ResolveSampleProjectRoot_UsesBundledSampleProjectUnderBaseDirectory()
    {
        var sampleProject = CreateSampleProject(Path.Combine(_root, "SampleProject"));

        var resolved = AgentWorkflowPaths.ResolveSampleProjectRoot(_root, []);

        resolved.Should().Be(Path.GetFullPath(sampleProject));
    }

    [Fact]
    public void ResolveSampleProjectRoot_UsesExplicitCliOverride()
    {
        var sampleProject = CreateSampleProject(Path.Combine(_root, "CustomSample"));

        var resolved = AgentWorkflowPaths.ResolveSampleProjectRoot(
            Path.Combine(_root, "bin"),
            ["--project-root", sampleProject]);

        resolved.Should().Be(Path.GetFullPath(sampleProject));
    }

    [Fact]
    public void ResolveSampleProjectRoot_UsesInlineCliOverride()
    {
        var sampleProject = CreateSampleProject(Path.Combine(_root, "InlineSample"));

        var resolved = AgentWorkflowPaths.ResolveSampleProjectRoot(
            Path.Combine(_root, "bin"),
            [$"--project-root={sampleProject}"]);

        resolved.Should().Be(Path.GetFullPath(sampleProject));
    }

    [Fact]
    public void ResolveSampleProjectRoot_UsesPositionalOverride()
    {
        var sampleProject = CreateSampleProject(Path.Combine(_root, "PositionalSample"));

        var resolved = AgentWorkflowPaths.ResolveSampleProjectRoot(
            Path.Combine(_root, "bin"),
            [sampleProject]);

        resolved.Should().Be(Path.GetFullPath(sampleProject));
    }

    [Fact]
    public void ValidateSampleProjectRoot_Throws_WhenAgentsAreMissing()
    {
        var sampleProject = Path.Combine(_root, "SampleProject");
        Directory.CreateDirectory(Path.Combine(sampleProject, "src"));
        File.WriteAllText(Path.Combine(sampleProject, "src", "TicketService.cs"), "public class TicketService { }");
        File.WriteAllText(Path.Combine(sampleProject, "Acme.Tickets.csproj"), "<Project />");

        var act = () => AgentWorkflowPaths.ValidateSampleProjectRoot(sampleProject);

        act.Should().Throw<DirectoryNotFoundException>()
            .WithMessage("*custom agents*");
    }

    [Fact]
    public void ValidateSampleProjectRoot_Throws_WhenSourcesAreMissing()
    {
        var sampleProject = Path.Combine(_root, "SampleProject");
        Directory.CreateDirectory(Path.Combine(sampleProject, ".github", "agents"));
        File.WriteAllText(Path.Combine(sampleProject, ".github", "agents", "test-planner.md"), "---\nname: test-planner\n---\nPrompt");
        File.WriteAllText(Path.Combine(sampleProject, "Acme.Tickets.csproj"), "<Project />");

        var act = () => AgentWorkflowPaths.ValidateSampleProjectRoot(sampleProject);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*source files*");
    }

    [Fact]
    public void ValidateSampleProjectRoot_Throws_WhenProjectFileIsMissing()
    {
        var sampleProject = Path.Combine(_root, "SampleProject");
        Directory.CreateDirectory(Path.Combine(sampleProject, ".github", "agents"));
        Directory.CreateDirectory(Path.Combine(sampleProject, "src"));
        File.WriteAllText(Path.Combine(sampleProject, ".github", "agents", "test-planner.md"), "---\nname: test-planner\n---\nPrompt");
        File.WriteAllText(Path.Combine(sampleProject, "src", "TicketService.cs"), "public class TicketService { }");

        var act = () => AgentWorkflowPaths.ValidateSampleProjectRoot(sampleProject);

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*.csproj*");
    }

    private static string CreateSampleProject(string projectRoot)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, ".github", "agents"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        File.WriteAllText(Path.Combine(projectRoot, ".github", "agents", "test-planner.md"), "---\nname: test-planner\n---\nPrompt");
        File.WriteAllText(Path.Combine(projectRoot, "src", "TicketService.cs"), "public class TicketService { }");
        File.WriteAllText(Path.Combine(projectRoot, "Acme.Tickets.csproj"), "<Project />");
        return projectRoot;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
