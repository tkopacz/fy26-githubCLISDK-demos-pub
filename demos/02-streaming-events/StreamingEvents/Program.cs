// Demo 02 — StreamingEvents: Generator dziennika zmian
//
// Pokazuje: streaming token po tokenie + kolorowe eventy SDK.
// Cel: wklej git log --oneline → dostaniesz CHANGELOG.md w Keep a Changelog formacie.
// Każdy typ eventu ma inny kolor — widać jak SDK przetwarza odpowiedź.
// Uruchomienie: dotnet run --project demos/02-streaming-events/StreamingEvents

using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using CopilotSDK.Demos.StreamingEvents;
using GitHub.Copilot;
using Spectre.Console;

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    StreamingEventsCliHelper.PrintUsage();
    return 0;
}

ConsoleRenderer.Banner("Changelog Gen", "Demo 02 — GitHub Copilot SDK: Streaming Events");

string? rawGitLog = null;
if (options.Interactive)
{
    AnsiConsole.MarkupLine("[dim]Wklej wyjście git log --oneline (Enter + Ctrl+Z/Ctrl+D na końcu):[/]\n");
    rawGitLog = ReadMultilineInput();

    if (string.IsNullOrWhiteSpace(rawGitLog))
        ConsoleRenderer.Warn("Używam przykładowych commitów, ponieważ stdin nie dostarczył danych.");
}

var gitLog = StreamingEventsCliHelper.ResolveGitLog(options.Interactive, rawGitLog);

// Klient jest właścicielem połączenia ze środowiskiem uruchomieniowym SDK. Streaming nie wymaga
// specjalnego klienta; włącza się go w poniższej sesji CopilotSession.
await using var client = CopilotClientFactory.Create();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    // Streaming nakazuje SDK emitować zdarzenia AssistantMessageDeltaEvent w trakcie
    // generowania odpowiedzi przez model. Bez tego demo normalnie widziałoby tylko
    // ukończone AssistantMessageEvent na koniec tury.
    Streaming = true,
    // Obsługa uprawnień jest nadal częścią każdej konfiguracji sesji. To demo nie
    // rejestruje narzędzi, więc handler jest tu głównie po to, aby pokazać standardowe
    // miejsce, w którym podpina się politykę uprawnień SDK.
    OnPermissionRequest = PermissionHandler.ApproveAll,
});

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("▶ Eventy SDK (w czasie rzeczywistym)");
AnsiConsole.MarkupLine("[dim]Kolory: [yellow]⚙ TOOL[/] [blue]◎ IDLE[/] [green]token streaming[/] [red]error[/][/]\n");

var changelogBuilder = new System.Text.StringBuilder();
var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var toolCount = 0;
var tokenCount = 0;
var startTime = DateTime.UtcNow;
var userCancelled = false;
var timedOut = false;
using var cts = new CancellationTokenSource();
using var timeoutTimer = new Timer(
    _ =>
    {
        timedOut = true;
        cts.Cancel();
    },
    null,
    TimeSpan.FromMinutes(3),
    Timeout.InfiniteTimeSpan);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    userCancelled = true;
    cts.Cancel();
};
using var cancellationRegistration = cts.Token.Register(() => done.TrySetCanceled(cts.Token));

// Subskrybuj przed wysłaniem monitu. SDK może zacząć emitować zdarzenia zaraz po
// rozpoczęciu SendAsync, więc zarejestrowanie handlera w pierwszej kolejności zapobiega
// pominięciu wczesnych zdarzeń SessionStartEvent, delt, narzędzi czy błędów.
using var _ = session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case AssistantMessageDeltaEvent delta:
            // Każda delta zawiera tylko nowo wygenerowany fragment tekstu. Aplikacja
            // decyduje, czy go wydrukować, zbuforować, przekształcić, czy przesłać
            // ten fragment do innego transportu, takiego jak SSE.
            tokenCount++;
            changelogBuilder.Append(delta.Data.DeltaContent);
            // Wydrukuj fragment natychmiast, aby uzyskać efekt „pisania”, jednocześnie
            // buforując go, aby później zapisać pełny changelog na dysku.
            AnsiConsole.Markup($"[green]{delta.Data.DeltaContent.Replace("[", "[[").Replace("]", "]]")}[/]");
            break;

        case AssistantMessageEvent:
            // Końcowe zdarzenie wiadomości oznacza ukończony tekst asystenta. To demo
            // złożyło już treść z delt, więc używa tego zdarzenia tylko jako
            // wizualnego podziału linii.
            AnsiConsole.WriteLine();
            break;

        case ToolExecutionStartEvent tool:
            // Jeśli narzędzia są zarejestrowane w SessionConfig, ten sam strumień zdarzeń
            // pokazuje, kiedy model żąda wywołania narzędzia. To demo nie ma własnych
            // narzędzi, ale rejestrowanie zdarzenia sprawia, że cykl życia jest widoczny,
            // gdyby domyślne ustawienia SDK kiedykolwiek wyemitowały aktywność narzędzia.
            toolCount++;
            AnsiConsole.MarkupLine($"\n[yellow]⚙ TOOL [[{toolCount}]][/] {tool.Data.ToolName.Replace("[", "[[").Replace("]", "]]")}");
            break;

        case ToolExecutionCompleteEvent tool:
            // Zdarzenia zakończenia narzędzia przenoszą wynik po stronie hosta z powrotem do
            // rozmowy. Tutaj są one liczone/rejestrowane tylko po to, aby pokazać
            // sekwencję zdarzeń.
            AnsiConsole.MarkupLine($"[dim]   ↳ done ({tool.Data.ToolCallId[..8]}...)[/]");
            break;

        case SessionIdleEvent:
            // SessionIdleEvent to sygnał SDK, że tura się zakończyła i nie należy
            // oczekiwać kolejnych delt. Zwalnia poniższy TaskCompletionSource,
            // na który czeka aplikacja konsolowa.
            AnsiConsole.WriteLine();
            done.TrySetResult();
            break;

        case SessionErrorEvent err:
            // Błędy SDK/runtime również pojawiają się jako zdarzenia. Konwersja ich na
            // oczekiwane zadanie utrzymuje przepływ sterowania równoważny z rzucającym
            // wyjątki API żądanie/odpowiedź, jednocześnie demonstrując obsługę zdarzeń.
            ConsoleRenderer.Error(err.Data.Message);
            done.TrySetException(new Exception(err.Data.Message));
            break;
    }
});

var prompt = $"""
Based on this git log, generate a CHANGELOG.md following the "Keep a Changelog" format
(https://keepachangelog.com). Group commits by: Added, Changed, Fixed, Security.
Use today's date. Include the version header [Unreleased].

Git log:
{gitLog}
""";

try
{
    // SendAsync rozpoczyna turę SDK i powraca po przekazaniu monitu do środowiska
    // wykonawczego. Nie oznacza to, że asystent jest już gotowy; aplikacja czeka na
    // SessionIdleEvent za pośrednictwem powyższego TaskCompletionSource.
    await session.SendAsync(new MessageOptions { Prompt = prompt }, cts.Token);
    await done.Task.WaitAsync(cts.Token);
}
catch (OperationCanceledException) when (userCancelled)
{
    ConsoleRenderer.Warn("Anulowano przez Ctrl+C. Uruchom demo ponownie, aby wygenerować changelog.");
    return 1;
}
catch (OperationCanceledException) when (timedOut)
{
    ConsoleRenderer.Error("Copilot nie zakończył streamingu w ciągu 3 minut.");
    ConsoleRenderer.Info("Spróbuj ponownie albo sprawdź logowanie Copilot CLI i połączenie sieciowe.");
    return 1;
}

// Zapisz CHANGELOG.md bez nadpisywania plików użytkownika.
var outputPath = GetAvailableOutputPath();
await using (var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
await using (var writer = new StreamWriter(outputStream))
{
    await writer.WriteAsync(changelogBuilder.ToString());
}

var elapsed = DateTime.UtcNow - startTime;
AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Podsumowanie");
ConsoleRenderer.Table(
    new[]
    {
        ("Tokeny", tokenCount.ToString()),
        ("Narzędzia", toolCount.ToString()),
        ("Czas", $"{elapsed.TotalSeconds:F1}s"),
        ("Output", outputPath),
    },
    ("Metryka", x => x.Item1),
    ("Wartość", x => x.Item2));

ConsoleRenderer.Success($"CHANGELOG.md zapisany do: {outputPath}");
return 0;

static string GetAvailableOutputPath()
{
    var outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "demo-output", "02-streaming-events");
    Directory.CreateDirectory(outputDirectory);

    var outputPath = Path.Combine(outputDirectory, "CHANGELOG.md");
    if (!File.Exists(outputPath))
    {
        return outputPath;
    }

    for (var index = 1; ; index++)
    {
        outputPath = Path.Combine(outputDirectory, $"CHANGELOG-{index}.md");
        if (!File.Exists(outputPath))
        {
            return outputPath;
        }
    }
}

static string ReadMultilineInput()
{
    var sb = new System.Text.StringBuilder();
    string? line;
    while ((line = Console.ReadLine()) != null)
        sb.AppendLine(line);
    return sb.ToString();
}

