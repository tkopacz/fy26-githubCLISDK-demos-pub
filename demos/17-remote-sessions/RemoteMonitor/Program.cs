using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;

ConsoleRenderer.Banner("Remote Monitor", "Demo 17 — GitHub Copilot SDK: observe sessions from a second client");

var handshake = await CopilotClientFactory.LoadRemoteRuntimeHandshakeAsync();
if (handshake is null)
{
    ConsoleRenderer.Error("Brak aktywnego pliku handshake runtime albo handshake wygasł. Uruchom najpierw RemoteServer.");
    return 1;
}

// Monitor to drugi proces klienta SDK podłączony do tego samego zdalnego środowiska
// wykonawczego. Nie jest właścicielem sesji; obserwuje metadane udostępnione przez
// serwer środowiska wykonawczego.
await using var client = CopilotClientFactory.CreateRemoteClient($"{handshake.Host}:{handshake.Port}", handshake.ConnectionToken);
await client.StartAsync();

// ListSessionsAsync odpytuje rejestr sesji na poziomie środowiska wykonawczego, dzięki czemu
// widzi sesje rozpoczęte przez RemoteWorker, mimo że ten proces ich nie utworzył.
var sessions = await client.ListSessionsAsync(new SessionListFilter());
ConsoleRenderer.Success($"Policzono {sessions.Count} sesji na runtime.");

if (sessions.Count == 0)
{
    ConsoleRenderer.Warn("Na razie nie ma aktywnych sesji do obserwacji.");
    return 0;
}

ConsoleRenderer.Table(
    sessions,
    ("Session", session => session.SessionId[..Math.Min(12, session.SessionId.Length)] + "..."),
    ("Summary", session => session.Summary ?? "(no summary)"),
    ("Remote", session => session.IsRemote.ToString()),
    ("Updated", session => session.ModifiedTime.ToString("O")),
    ("Started", session => session.StartTime.ToString("O")));

var sessionState = await CopilotClientFactory.LoadRemoteSessionStateAsync();
if (sessionState is not null)
{
    ConsoleRenderer.Info($"Najnowsza sesja robocza: {sessionState.SessionId}");
    // Wyszukiwanie metadanych to kolejna operacja SDK na poziomie klienta. Sprawdza
    // zdalną sesję bez wznawiania rozmowy i wysyłania monitu.
    var metadata = await client.GetSessionMetadataAsync(sessionState.SessionId);
    if (metadata is not null)
    {
        ConsoleRenderer.Info($"Summary: {metadata.Summary}");
        ConsoleRenderer.Info($"Remote: {metadata.IsRemote}");
    }
}

return 0;
