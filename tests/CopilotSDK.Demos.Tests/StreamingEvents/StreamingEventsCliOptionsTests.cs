using CopilotSDK.Demos.StreamingEvents;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.StreamingEvents;

public sealed class StreamingEventsCliOptionsTests
{
    [Fact]
    public void Parse_EnablesInteractiveMode()
    {
        var options = CliOptions.Parse(["--interactive"]);

        options.Interactive.Should().BeTrue();
        options.ShowHelp.Should().BeFalse();
    }

    [Fact]
    public void Parse_EnablesInteractiveModeWithShortAlias()
    {
        var options = CliOptions.Parse(["-i"]);

        options.Interactive.Should().BeTrue();
    }

    [Fact]
    public void ResolveGitLog_UsesSampleLogByDefault()
    {
        var resolved = StreamingEventsCliHelper.ResolveGitLog(interactive: false, input: "custom git log");

        resolved.Should().Be(StreamingEventsCliHelper.GetSampleGitLog());
    }

    [Fact]
    public void ResolveGitLog_UsesProvidedInputWhenInteractive()
    {
        var resolved = StreamingEventsCliHelper.ResolveGitLog(interactive: true, input: "custom git log");

        resolved.Should().Be("custom git log");
    }
}
