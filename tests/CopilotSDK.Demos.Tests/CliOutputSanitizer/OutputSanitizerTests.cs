using CopilotSDK.Demos.Demos.CliOutputSanitizer;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.CliOutputSanitizer;

public sealed class OutputSanitizerTests
{
    [Fact]
    public void Sanitize_ReturnsEmptyString_WhenOutputIsWhitespace()
    {
        var result = OutputSanitizer.Sanitize(" \r\n\t ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_RemovesAnsiCodesDecorativeLinesAndExcessWhitespace()
    {
        var raw = "  \u001b[31mERROR\u001b[0m    build\t\tfailed\r\n\r\n  ----------\n\n  dotnet   test  ";

        var result = OutputSanitizer.Sanitize(raw);

        result.Should().Be(string.Join(
            Environment.NewLine,
            "ERROR build failed",
            string.Empty,
            "dotnet test"));
    }

    [Fact]
    public void Sanitize_RemovesOnlyDecorativeNoise()
    {
        var raw = "\n  =======  \n\t***\t\n";

        var result = OutputSanitizer.Sanitize(raw);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_NormalizesMixedNewlines()
    {
        var raw = "first\rsecond\r\nthird\n\nfourth";

        var result = OutputSanitizer.Sanitize(raw);

        result.Should().Be(string.Join(
            Environment.NewLine,
            "first",
            "second",
            "third",
            string.Empty,
            "fourth"));
    }
}
