using System.Text.Json;
using CopilotSDK.Demos.GuardedCopilotCli;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.GuardedCopilotCli;

public sealed class SecretFolderGuardPolicyTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("guarded-cli-").FullName;

    [Fact]
    public void EvaluatePreToolUse_AllowsNormalFilesInsideWorkspace()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs("src/Allowed.cs"),
            _workspaceRoot);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesRootTajnePaths()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs("TAJNE/secret.txt"),
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("TAJNE");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesNestedTajnePaths()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "write_workspace_file",
            BuildPathArgs("nested/TAJNE/leak.txt"),
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("TAJNE");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesCaseInsensitiveTajnePaths()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs("nested/tajne/credentials.txt"),
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("TAJNE");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesTraversalOutsideWorkspace()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs("../escape.txt"),
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("workspace");
    }

    [Fact]
    public void EvaluatePreToolUse_AllowsSdkMetadataIntentTool()
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "report_intent",
            """{"intent":"list files"}""",
            _workspaceRoot);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Contain("Metadata");
    }

    [Fact]
    public void IsPermissionKindAllowed_AllowsSdkMetadataIntentTool()
    {
        SecretFolderGuardPolicy.IsPermissionKindAllowed("runtime.report_intent")
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("powershell")]
    [InlineData("task")]
    [InlineData("unknown_tool")]
    public void IsPermissionKindAllowed_DeniesShellAndUnknownTools(string toolName)
    {
        SecretFolderGuardPolicy.IsPermissionKindAllowed(toolName)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("powershell")]
    [InlineData("run_command")]
    public void EvaluatePreToolUse_DeniesShellToolsEvenWithAllowedPath(string toolName)
    {
        var decision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            toolName,
            BuildPathArgs("public/notes.txt"),
            _workspaceRoot);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Shell");
    }

    [Fact]
    public void EvaluatePreToolUse_DeniesAbsoluteUncAndHomeRelativePaths()
    {
        var absolutePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "outside.txt"));
        var absoluteDecision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs(absolutePath),
            _workspaceRoot);
        absoluteDecision.Allowed.Should().BeFalse();
        absoluteDecision.Reason.Should().Contain("Absolute");

        var uncDecision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs(@"\\server\share\secret.txt"),
            _workspaceRoot);
        uncDecision.Allowed.Should().BeFalse();
        uncDecision.Reason.Should().Contain("network");

        var homeDecision = SecretFolderGuardPolicy.EvaluatePreToolUse(
            "read_workspace_file",
            BuildPathArgs(@"~\secret.txt"),
            _workspaceRoot);
        homeDecision.Allowed.Should().BeFalse();
        homeDecision.Reason.Should().Contain("home-relative");
    }

    [Fact]
    public void ListWorkspaceFiles_SkipsProtectedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "public"));
        File.WriteAllText(Path.Combine(_workspaceRoot, "public", "notes.txt"), "public");
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "TAJNE"));
        File.WriteAllText(Path.Combine(_workspaceRoot, "TAJNE", "secret.txt"), "secret");
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "nested", "TAJNE"));
        File.WriteAllText(Path.Combine(_workspaceRoot, "nested", "TAJNE", "credentials.txt"), "creds");

        var list = SecretFolderGuardPolicy.ListWorkspaceFiles(_workspaceRoot, ".", 50);

        list.Blocked.Should().BeFalse();
        list.Files.Should().Contain("public/notes.txt");
        list.Files.Should().NotContain(path => path.Contains("TAJNE", StringComparison.OrdinalIgnoreCase));
        list.BlockedDirectories.Should().Contain("TAJNE");
        list.BlockedDirectories.Should().Contain("nested/TAJNE");
    }

    [Fact]
    public void WriteWorkspaceFile_DoesNotCreateFileWhenTargetIsProtected()
    {
        var blockedPath = Path.Combine(_workspaceRoot, "nested", "TAJNE", "leak.txt");

        var result = SecretFolderGuardPolicy.WriteWorkspaceFile(
            _workspaceRoot,
            "nested/TAJNE/leak.txt",
            "exfiltrate",
            overwrite: false);

        result.Blocked.Should().BeTrue();
        result.Reason.Should().Contain("TAJNE");
        File.Exists(blockedPath).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }

    private static string BuildPathArgs(string path) =>
        JsonSerializer.Serialize(new { path });
}
