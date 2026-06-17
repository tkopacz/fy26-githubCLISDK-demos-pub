using CopilotSDK.Demos.Demos.AgentWorkflows;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.AgentWorkflows;

public sealed class StaticAnalysisTests : IDisposable
{
    private readonly string _projectRoot = Directory.CreateTempSubdirectory("static-analysis-").FullName;

    [Fact]
    public void Analyze_ReportsDeterministicFindings()
    {
        WriteFile("src/TicketService.cs", """
            public sealed class TicketService
            {
                // TODO: add validation
                public void Create() { }
            }
            """);
        WriteFile("src/bad_name.cs", """
            public sealed class BadName
            {
                private const string password = "abc12345";
            }
            """);
        WriteFile("README.md", "HACK: document architecture\n\n");
        WriteFile("Acme.Tickets.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="FluentAssertions" Version="8.10.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="fluentassertions" Version="8.10.0" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(Path.Combine("bin", "Debug", "Ignored.cs"), "public class ignored_secret { string token = \"abc12345\"; }");
        WriteFile(Path.Combine("obj", "Debug", "Ignored.cs"), "// FIXME ignored");
        WriteFile(Path.Combine(".git", "hooks", "Ignored.cs"), "public class ignored_name { }");

        var report = StaticAnalysis.Analyze(_projectRoot);

        report.TotalFiles.Should().Be(4);
        report.NonEmptyLines.Should().Be(17);
        report.TodoFiles.Should().Equal("README.md", "src/TicketService.cs");
        report.PotentialSecretFiles.Should().Equal("src/bad_name.cs");
        report.NamingViolations.Should().Equal("src/bad_name.cs");
        report.PackageReferences.Should().Equal("FluentAssertions", "xunit");
    }

    [Fact]
    public void ToMarkdown_IncludesOptionalDetails_WhenFindingsExist()
    {
        var report = new StaticAnalysisReport(
            TotalFiles: 2,
            NonEmptyLines: 10,
            TodoFiles: ["src/TicketService.cs"],
            PotentialSecretFiles: ["src/Secrets.cs"],
            NamingViolations: ["src/bad_name.cs"],
            PackageReferences: ["FluentAssertions", "xunit"]);

        var markdown = report.ToMarkdown();

        markdown.Should().Contain("- Total files scanned: 2");
        markdown.Should().Contain("- Files with TODO/FIXME/HACK: 1");
        markdown.Should().Contain("- Package references: FluentAssertions, xunit");
        markdown.Should().Contain("- TODO files: src/TicketService.cs");
        markdown.Should().Contain("- Potential secret files: src/Secrets.cs");
        markdown.Should().Contain("- Naming violations: src/bad_name.cs");
    }

    public void Dispose()
    {
        Directory.Delete(_projectRoot, recursive: true);
    }

    private void WriteFile(string relativePath, string contents)
    {
        var path = Path.Combine(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
