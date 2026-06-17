using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;

ConsoleRenderer.Banner("Remote Sessions", "Demo 17 — GitHub Copilot SDK: runtime server + disconnected sessions");

var connectionToken = Environment.GetEnvironmentVariable("REMOTE_CONNECTION_TOKEN") ?? Guid.NewGuid().ToString("N");
var tokenFingerprint = CopilotClientFactory.CreateSecretFingerprint(connectionToken);
ConsoleRenderer.Info("Uruchamiam serwer runtime Copilota na TCP. Ten proces ma być otwarty w osobnym terminalu.");

// CreateRemoteServer konfiguruje środowisko wykonawcze SDK do nasłuchiwania na TCP zamiast
// używania go wyłącznie w tym procesie. Inne instancje CopilotClient mogą połączyć się
// z tym samym środowiskiem wykonawczym przy użyciu tokenu połączenia.
await using var runtimeClient = CopilotClientFactory.CreateRemoteServer(port: 0, connectionToken: connectionToken);
await runtimeClient.StartAsync();

var actualPort = runtimeClient.RuntimePort ?? 0;
if (actualPort <= 0)
{
    ConsoleRenderer.Error("Nie udało się ustalić portu runtime. Zatrzymuję serwer.");
    return 1;
}

var runtimeUrl = $"http://127.0.0.1:{actualPort}";
// Handshake nie jest historią rozmów SDK. To tylko metadane wykrywania,
// które informują procesy robocze/monitorujące, gdzie znajduje się zdalne środowisko
// wykonawcze i jak się w nim uwierzytelnić.
var handshake = new CopilotClientFactory.RemoteRuntimeHandshake(
    Host: "127.0.0.1",
    Port: actualPort,
    ConnectionToken: connectionToken,
    StartedAtUtc: DateTimeOffset.UtcNow,
    ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(30));

var handshakeFile = await CopilotClientFactory.SaveRemoteRuntimeHandshakeAsync(handshake);

ConsoleRenderer.Success($"Runtime server gotowy: {runtimeUrl}");
ConsoleRenderer.Info($"Connection token fingerprint: {tokenFingerprint}");
ConsoleRenderer.Info($"Handshake file: {handshakeFile}");
ConsoleRenderer.Info($"Handshake expires: {handshake.ExpiresAtUtc:O}");
ConsoleRenderer.Info("Użyj RemoteWorker do uruchomienia długiego obliczenia i RemoteMonitor do obserwacji sesji.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.IsCancellationRequested)
    {
        // ListSessionsAsync to wywołanie SDK na poziomie klienta względem zdalnego
        // środowiska wykonawczego. Pozwala temu procesowi serwera pokazywać sesje rozpoczęte
        // przez inne procesy połączone z tym samym środowiskiem wykonawczym.
        var sessions = await runtimeClient.ListSessionsAsync(new SessionListFilter(), cts.Token);
        if (sessions.Count > 0)
        {
            ConsoleRenderer.Table(
                sessions.Take(10),
                ("Session", session => session.SessionId[..Math.Min(12, session.SessionId.Length)] + "..."),
                ("Summary", session => session.Summary ?? "(no summary)"),
                ("Remote", session => session.IsRemote.ToString()),
                ("Updated", session => session.ModifiedTime.ToString("O")));
        }

        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
    }
}
catch (OperationCanceledException)
{
    ConsoleRenderer.Warn("Serwer runtime zatrzymany przez użytkownika.");
}
finally
{
    CopilotClientFactory.DeleteRemoteRuntimeHandshake();
    CopilotClientFactory.DeleteRemoteSessionState();
}

return 0;
