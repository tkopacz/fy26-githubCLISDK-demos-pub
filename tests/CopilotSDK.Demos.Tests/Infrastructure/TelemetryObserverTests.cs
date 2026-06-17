using CopilotSDK.Demos.Shared.Infrastructure;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.Infrastructure;

public sealed class TelemetryObserverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("telemetry-observer-tests-").FullName;

    [Fact]
    public async Task Observer_EmitsEntry_ForValidSpanRecord()
    {
        var filePath = Path.Combine(_tempDir, "valid-span.jsonl");
        await File.WriteAllTextAsync(filePath, string.Empty);

        await using var observer = new TelemetryObserver(filePath);

        var line = """
            {"type":"span","name":"rpc.call","startTime":[1700000000,0],"endTime":[1700000001,500000000],"attributes":{"http.method":"GET","retry":2,"ok":true}}
            """;
        await AppendLineAsync(filePath, line);

        var entry = await ReadOneAsync(observer, TimeSpan.FromSeconds(5));

        entry.Name.Should().Be("rpc.call");
        entry.Duration.Should().NotBeNull();
        entry.Duration!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
        entry.Tags.Should().Contain(x => x.Key == "http.method" && x.Value == "GET");
        entry.Tags.Should().Contain(x => x.Key == "retry" && x.Value == "2");
        entry.Tags.Should().Contain(x => x.Key == "ok" && x.Value == "true");
    }

    [Fact]
    public async Task Observer_IgnoresInvalidJsonAndNonSpanRecords()
    {
        var filePath = Path.Combine(_tempDir, "invalid-and-nonspan.jsonl");
        await File.WriteAllTextAsync(filePath, string.Empty);

        await using var observer = new TelemetryObserver(filePath);
        await AppendLineAsync(filePath, "{ not json }");
        await AppendLineAsync(filePath, "{\"type\":\"log\",\"name\":\"not-a-span\"}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var act = async () => await observer.Entries.ReadAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Observer_HandlesPartialLinesAcrossAppends()
    {
        var filePath = Path.Combine(_tempDir, "partial-lines.jsonl");
        await File.WriteAllTextAsync(filePath, string.Empty);

        await using var observer = new TelemetryObserver(filePath);

        var firstHalf = "{\"type\":\"span\",\"name\":\"split\",\"startTime\":[1700000000,0]";
        var secondHalf = ",\"endTime\":[1700000000,100000000],\"attributes\":{\"piece\":\"ok\"}}";

        await File.AppendAllTextAsync(filePath, firstHalf);
        await Task.Delay(200);
        await File.AppendAllTextAsync(filePath, secondHalf + Environment.NewLine);

        var entry = await ReadOneAsync(observer, TimeSpan.FromSeconds(5));

        entry.Name.Should().Be("split");
        entry.Tags.Should().Contain(x => x.Key == "piece" && x.Value == "ok");
    }

    [Fact]
    public async Task Observer_StartsFromEndOfExistingFile()
    {
        var filePath = Path.Combine(_tempDir, "existing-lines.jsonl");
        await AppendLineAsync(filePath,
            "{\"type\":\"span\",\"name\":\"old\",\"startTime\":[1700000000,0],\"endTime\":[1700000000,0],\"attributes\":{}}\n");

        await using var observer = new TelemetryObserver(filePath);

        await AppendLineAsync(filePath,
            "{\"type\":\"span\",\"name\":\"new\",\"startTime\":[1700000001,0],\"endTime\":[1700000002,0],\"attributes\":{}}\n");

        var entry = await ReadOneAsync(observer, TimeSpan.FromSeconds(5));
        entry.Name.Should().Be("new");
    }

    [Fact]
    public async Task Stop_CompletesChannelReader()
    {
        var filePath = Path.Combine(_tempDir, "stop.jsonl");
        await File.WriteAllTextAsync(filePath, string.Empty);

        await using var observer = new TelemetryObserver(filePath);
        observer.Stop();

        var completed = await observer.Entries.WaitToReadAsync(CancellationToken.None);

        completed.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static async Task AppendLineAsync(string filePath, string line)
    {
        await File.AppendAllTextAsync(filePath, line.TrimEnd('\r', '\n') + Environment.NewLine);
    }

    private static async Task<TelemetryObserver.RpcEntry> ReadOneAsync(TelemetryObserver observer, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await observer.Entries.ReadAsync(cts.Token);
    }
}