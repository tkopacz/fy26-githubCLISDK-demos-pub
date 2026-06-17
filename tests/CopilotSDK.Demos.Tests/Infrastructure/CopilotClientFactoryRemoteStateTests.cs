using System.Text.Json;
using CopilotSDK.Demos.Shared.Infrastructure;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.Infrastructure;

public sealed class CopilotClientFactoryRemoteStateTests : IDisposable
{
    private readonly string _runtimeStateDir = CopilotClientFactory.RuntimeStateDirectory;
    private readonly string _handshakePointerPath;
    private readonly string _sessionPointerPath;
    private readonly string _legacyHandshakePath;
    private readonly string _legacySessionPath;

    private readonly string? _handshakePointerBackup;
    private readonly string? _sessionPointerBackup;
    private readonly string? _legacyHandshakeBackup;
    private readonly string? _legacySessionBackup;

    public CopilotClientFactoryRemoteStateTests()
    {
        _handshakePointerPath = Path.Combine(_runtimeStateDir, "copilot-remote-runtime-handshake.current");
        _sessionPointerPath = Path.Combine(_runtimeStateDir, "copilot-remote-session-state.current");
        _legacyHandshakePath = Path.Combine(_runtimeStateDir, "copilot-remote-runtime-handshake.json");
        _legacySessionPath = Path.Combine(_runtimeStateDir, "copilot-remote-session-state.json");

        _handshakePointerBackup = BackupIfExists(_handshakePointerPath);
        _sessionPointerBackup = BackupIfExists(_sessionPointerPath);
        _legacyHandshakeBackup = BackupIfExists(_legacyHandshakePath);
        _legacySessionBackup = BackupIfExists(_legacySessionPath);

        CopilotClientFactory.DeleteRemoteRuntimeHandshake();
        CopilotClientFactory.DeleteRemoteSessionState();
    }

    [Fact]
    public async Task SaveRemoteSessionStateAsync_LoadRemoteSessionStateAsync_RoundTripsAndSanitizesSecrets()
    {
        var state = new CopilotClientFactory.RemoteSessionState(
            SessionId: "session-123",
            RuntimeUrl: "tcp://127.0.0.1:7777",
            StartedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10),
            ConnectionToken: "secret-token",
            Prompt: "sensitive prompt");

        var path = await CopilotClientFactory.SaveRemoteSessionStateAsync(state);
        var loaded = await CopilotClientFactory.LoadRemoteSessionStateAsync();

        File.Exists(path).Should().BeTrue();
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("session-123");
        loaded.RuntimeUrl.Should().Be("tcp://127.0.0.1:7777");
        loaded.ConnectionToken.Should().BeNull();
        loaded.Prompt.Should().BeNull();
    }

    [Fact]
    public async Task LoadRemoteSessionStateAsync_ReturnsNullAndDeletesFile_WhenStateExpired()
    {
        var expired = new CopilotClientFactory.RemoteSessionState(
            SessionId: "expired-session",
            RuntimeUrl: "tcp://127.0.0.1:8888",
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var path = await CopilotClientFactory.SaveRemoteSessionStateAsync(expired);

        var loaded = await CopilotClientFactory.LoadRemoteSessionStateAsync();

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task SaveRemoteRuntimeHandshakeAsync_LoadRemoteRuntimeHandshakeAsync_RoundTrips()
    {
        var handshake = new CopilotClientFactory.RemoteRuntimeHandshake(
            Host: "127.0.0.1",
            Port: 4333,
            ConnectionToken: "connect-token",
            RuntimePath: "/tmp/copilot-runtime",
            StartedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10));

        var path = await CopilotClientFactory.SaveRemoteRuntimeHandshakeAsync(handshake);
        var loaded = await CopilotClientFactory.LoadRemoteRuntimeHandshakeAsync();

        File.Exists(path).Should().BeTrue();
        loaded.Should().NotBeNull();
        loaded!.Host.Should().Be("127.0.0.1");
        loaded.Port.Should().Be(4333);
        loaded.ConnectionToken.Should().Be("connect-token");
        loaded.RuntimePath.Should().Be("/tmp/copilot-runtime");
    }

    [Fact]
    public async Task LoadRemoteRuntimeHandshakeAsync_ReturnsNullAndDeletesFile_WhenExpired()
    {
        var expired = new CopilotClientFactory.RemoteRuntimeHandshake(
            Host: "localhost",
            Port: 5000,
            ConnectionToken: "expired-token",
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        var path = await CopilotClientFactory.SaveRemoteRuntimeHandshakeAsync(expired);

        var loaded = await CopilotClientFactory.LoadRemoteRuntimeHandshakeAsync();

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task LoadRemoteSessionStateAsync_ReturnsNull_WhenPointerJsonIsInvalid()
    {
        await File.WriteAllTextAsync(_sessionPointerPath, "{ invalid-json }");

        var loaded = await CopilotClientFactory.LoadRemoteSessionStateAsync();

        loaded.Should().BeNull();
        File.Exists(_sessionPointerPath).Should().BeFalse();
    }

    [Fact]
    public async Task LoadRemoteRuntimeHandshakeAsync_ReturnsNull_WhenPointerTargetsMissingFile()
    {
        var missingStatePath = Path.Combine(_runtimeStateDir, $"missing-{Guid.NewGuid():N}.json");
        var pointerJson = JsonSerializer.Serialize(new
        {
            path = missingStatePath,
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await File.WriteAllTextAsync(_handshakePointerPath, pointerJson);

        var loaded = await CopilotClientFactory.LoadRemoteRuntimeHandshakeAsync();

        loaded.Should().BeNull();
        File.Exists(_handshakePointerPath).Should().BeFalse();
    }

    [Fact]
    public void CreateSecretFingerprint_ReturnsDeterministic12CharHex()
    {
        var one = CopilotClientFactory.CreateSecretFingerprint("my-secret");
        var two = CopilotClientFactory.CreateSecretFingerprint("my-secret");
        var three = CopilotClientFactory.CreateSecretFingerprint("another-secret");

        one.Should().Be(two);
        one.Should().NotBe(three);
        one.Should().HaveLength(12);
        one.Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [Fact]
    public void DeleteTelemetryFile_DeletesFile_WhenPresent()
    {
        var path = CopilotClientFactory.TelemetryFilePath;
        File.WriteAllText(path, "telemetry");

        CopilotClientFactory.DeleteTelemetryFile();

        File.Exists(path).Should().BeFalse();
    }

    public void Dispose()
    {
        CopilotClientFactory.DeleteRemoteRuntimeHandshake();
        CopilotClientFactory.DeleteRemoteSessionState();

        RestoreBackup(_handshakePointerPath, _handshakePointerBackup);
        RestoreBackup(_sessionPointerPath, _sessionPointerBackup);
        RestoreBackup(_legacyHandshakePath, _legacyHandshakeBackup);
        RestoreBackup(_legacySessionPath, _legacySessionBackup);
    }

    private static string? BackupIfExists(string path)
    {
        if (!File.Exists(path))
            return null;

        var backupPath = path + ".test-backup-" + Guid.NewGuid().ToString("N");
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    private static void RestoreBackup(string destinationPath, string? backupPath)
    {
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        if (backupPath is null)
            return;

        if (File.Exists(backupPath))
        {
            File.Move(backupPath, destinationPath, overwrite: true);
        }
    }
}