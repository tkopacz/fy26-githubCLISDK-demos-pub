using CopilotSDK.Demos.PermissionHooks;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.PermissionHooks;

public sealed class PermissionHooksPolicyTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("permission-hooks-").FullName;

    [Fact]
    public void EvaluatePreToolUse_AllowsCsWritesInsideWorkspace()
    {
        var decision = PermissionHooksPolicy.EvaluatePreToolUse(
            "write_file",
            """{"path":"src/Refactored.cs"}""",
            _workspaceRoot);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesProtectedConfigWrites()
    {
        var decision = PermissionHooksPolicy.EvaluatePreToolUse(
            "edit_file",
            """{"path":"appsettings.json"}""",
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain(".cs");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesTraversalAttempts()
    {
        var decision = PermissionHooksPolicy.EvaluatePreToolUse(
            "create_file",
            """{"path":"../Escape.cs"}""",
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("workspace");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesShellTools()
    {
        var decision = PermissionHooksPolicy.EvaluatePreToolUse(
            "run_shell_command",
            """{"command":"dotnet test"}""",
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Shell");
    }

    [Fact]
    public void ListCSharpFiles_UsesOnlyWorkspaceRoot()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "src"));
        File.WriteAllText(Path.Combine(_workspaceRoot, "src", "Allowed.cs"), "public class Allowed { }");

        var outsideRoot = Directory.CreateTempSubdirectory("permission-hooks-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outsideRoot, "Outside.cs"), "public class Outside { }");

            var files = PermissionHooksPolicy.ListCSharpFiles(_workspaceRoot);

            files.Should().Contain("Allowed.cs");
            files.Should().NotContain("Outside.cs");
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_workspaceRoot, recursive: true);
    }
}
