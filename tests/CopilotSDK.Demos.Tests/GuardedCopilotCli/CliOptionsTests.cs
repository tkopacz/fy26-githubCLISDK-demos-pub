using FluentAssertions;

namespace CopilotSDK.Demos.Tests.GuardedCopilotCli;

public sealed class CliOptionsTests
{
    [Fact]
    public void Parse_EnablesInteractiveMode()
    {
        var options = global::CliOptions.Parse(["--interactive"]);

        options.Interactive.Should().BeTrue();
        options.Prompt.Should().BeNull();
    }

    [Fact]
    public void Parse_EnablesInteractiveModeWithShortAlias()
    {
        var options = global::CliOptions.Parse(["-i"]);

        options.Interactive.Should().BeTrue();
    }

    [Fact]
    public void Parse_CapturesPromptAndAgent()
    {
        var options = global::CliOptions.Parse(
            ["--interactive", "--agent", "guarded-file-operator", "--prompt", "Read TAJNE/secret.txt"]);

        options.Interactive.Should().BeTrue();
        options.AgentName.Should().Be("guarded-file-operator");
        options.Prompt.Should().Be("Read TAJNE/secret.txt");
    }
}
