// Demo 03 — UnderTheHood: śledzenie JSON-RPC
//
// Pokazuje: co SDK robi "pod spodem" przez OpenTelemetry ActivityListener.
// Dwa panele side-by-side: odpowiedź AI (lewy) i live trace operacji RPC (prawy).
// Na końcu: statystyki — ile operacji, latencja pierwszego tokenu, całkowity czas.
// Uruchomienie: dotnet run --project demos/03-under-the-hood/UnderTheHood

using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

ConsoleRenderer.Banner("Under The Hood", "Demo 03 — GitHub Copilot SDK: JSON-RPC Trace");
AnsiConsole.MarkupLine("[dim]Obserwuj co SDK wysyła do Copilot CLI przez plik telemetrii OTLP (JSONL).[/]\n");

var captureContent = args.Contains("--capture-content", StringComparer.OrdinalIgnoreCase);
var promptArgs = args
    .Where(arg => !arg.Equals("--capture-content", StringComparison.OrdinalIgnoreCase))
    .ToArray();
var telemetryFilePath = CopilotClientFactory.TelemetryFilePath;

if (captureContent)
    ConsoleRenderer.Warn("CaptureContent włączone przez --capture-content; trace zostanie usunięty po wyświetleniu.");
else
    ConsoleRenderer.Info("CaptureContent jest wyłączone. Użyj --capture-content tylko gdy świadomie chcesz zobaczyć treść promptów/odpowiedzi.");

// TelemetryObserver śledzi plik telemetryczny JSONL emitowany przez proces wykonawczy
// Copilot. Jest to niezależne od zdarzeń sesji.
// Pokazuje dane telemetryczne operacji runtime/RPC, podczas gdy SessionEvent obejmuje konwersację i cykl życia.
await using var observer = new TelemetryObserver(telemetryFilePath);

// Telemetria jest włączona na poziomie CopilotClientOptions, ponieważ opisuje
// połączenie w czasie wykonywania i aktywność JSON-RPC należącą do klienta, a nie tylko
// jedną rozmowę. Flaga captureContent kontroluje, czy monity/odpowiedzi są
// dołączane do tych spanów.
var client = CopilotClientFactory.Create(enableTelemetryFile: true, captureTelemetryContent: captureContent);
await using var _ = client;

// Sesja jest nadal granicą konwersacji. Streaming=true pozwala demonstracji mierzyć
// „czas do pierwszego tokenu” z AssistantMessageDeltaEvent, podczas gdy telemetria
// równolegle rejestruje niskopoziomowe operacje środowiska wykonawczego.
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    Streaming = true,
    OnPermissionRequest = PermissionHandler.ApproveAll,
});

var prompt = promptArgs.Length > 0
    ? string.Join(" ", promptArgs)
    : "Explain async/await in C# — how does the state machine work under the hood? Be concise.";

ConsoleRenderer.Rule($"Prompt: {prompt[..Math.Min(60, prompt.Length)]}...");

// Zbierz odpowiedź asystenta ze zdarzeń sesji SDK, podczas gdy telemetria jest
// gromadzona z pliku śledzenia środowiska wykonawczego. Celem jest porównanie obu
// widoków w tym demie „pod maską”.
var responseBuilder = new System.Text.StringBuilder();
var rpcEntries = new List<TelemetryObserver.RpcEntry>();
var firstTokenTime = (TimeSpan?)null;
var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var startTime = DateTime.UtcNow;

// Czytnik w tle dla wpisów telemetrycznych. Nie wywołuje SDK Copilot; jedynie
// opróżnia kanał obserwatora, aby spany RPC można było podsumować, gdy sesja
// przejdzie w stan bezczynności.
var traceTask = Task.Run(async () =>
{
    await foreach (var entry in observer.Entries.ReadAllAsync())
    {
        lock (rpcEntries) rpcEntries.Add(entry);
    }
});

using var evtSub = session.On<SessionEvent>(evt =>
{
    // Zdarzenia sesji to wysokopoziomowy kontrakt SDK dla tury. To demo wykorzystuje
    // je tylko do tekstu odpowiedzi oraz sygnalizacji zakończenia/błędu; pomiar czasu
    // niskopoziomowego RPC pochodzi z telemetrii.
    switch (evt)
    {
        case AssistantMessageDeltaEvent delta:
            // Pierwsza delta to przydatna metryka opóźnienia postrzeganego przez użytkownika:
            // środowisko wykonawcze zaakceptowało monit, a model zaczął odsyłać
            // tekst asystenta przez SDK.
            if (firstTokenTime is null)
                firstTokenTime = DateTime.UtcNow - startTime;
            responseBuilder.Append(delta.Data.DeltaContent);
            break;
        case SessionIdleEvent:
            // Bezczynność oznacza, że tura SDK się zakończyła, więc obserwator telemetrii
            // może przestać śledzić plik, a interfejs użytkownika może podsumować przechwycone spany.
            observer.Stop();
            done.TrySetResult();
            break;
        case SessionErrorEvent err:
            // Konwertuj zdarzenia błędów SDK na oczekiwane zadanie, aby interfejs użytkownika
            // kończył się niepowodzeniem w tym samym miejscu, w którym oczekuje normalnego zakończenia.
            observer.Stop();
            done.TrySetException(new Exception(err.Data.Message));
            break;
    }
});

AnsiConsole.WriteLine();
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Czekam na odpowiedź i trace RPC...", async ctx =>
    {
        ctx.Status("Copilot myśli... (obserwuję operacje RPC)");
        // SendAsync rozpoczyna turę sesji. Poniższe zadanie czeka na SessionIdleEvent
        // obserwowane przez subskrypcję zdarzeń, ponieważ samo SendAsync nie jest
        // API typu „zwróć ostateczną odpowiedź”.
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await done.Task;
        await traceTask;
    });

// Prezentacja wyników
AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Odpowiedź Copilot");
AnsiConsole.WriteLine(responseBuilder.ToString());

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Spany OTLP (odczytane z pliku JSONL)");

List<TelemetryObserver.RpcEntry> captured;
lock (rpcEntries) captured = new List<TelemetryObserver.RpcEntry>(rpcEntries);

if (captured.Count == 0)
{
    ConsoleRenderer.Warn("Brak spanów w pliku telemetrii — upewnij się, że enableTelemetryFile=true.");
    ConsoleRenderer.Info("Plik telemetrii: " + telemetryFilePath);
}
else
{
    ConsoleRenderer.Table(
        captured.Take(20),
        ("Operacja", e => e.Name.Length > 40 ? e.Name[..40] + "…" : e.Name),
        ("Start", e => e.StartTime.ToString("HH:mm:ss.fff")),
        ("Czas [ms]", e => e.Duration.HasValue ? $"{e.Duration.Value.TotalMilliseconds:F0}" : "?"));
}

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Statystyki sesji");

var totalTime = DateTime.UtcNow - startTime;
ConsoleRenderer.Table(
    new[]
    {
        ("Operacje RPC", captured.Count.ToString()),
        ("Latencja 1. tokenu", firstTokenTime.HasValue ? $"{firstTokenTime.Value.TotalMilliseconds:F0} ms" : "N/A"),
        ("Czas całkowity", $"{totalTime.TotalSeconds:F2} s"),
        ("Długość odpowiedzi", $"{responseBuilder.Length} znaków"),
        ("Model", session.SessionId[..8] + "..."),
    },
    ("Metryka", x => x.Item1),
    ("Wartość", x => x.Item2));

ConsoleRenderer.Info("Plik trace: " + telemetryFilePath); //PS + cat aby pokazać
CopilotClientFactory.DeleteTelemetryFile();
