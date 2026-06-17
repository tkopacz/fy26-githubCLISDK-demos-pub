// Demo 01 — HelloSession: Doradca zależności NuGet
//
// Pokazuje: minimalny lifecycle SDK — CopilotClient → CreateSessionAsync → SendAndWait → Dispose.
// Cel: 3 pytania w jednej sesji, model pamięta kontekst między nimi.
// Uruchomienie: dotnet run --project demos/01-hello-session/HelloSession

using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Dependency Advisor", "Demo 01 — GitHub Copilot SDK: HelloSession");
ConsoleRenderer.Info("Analizuję pakiety NuGet w 3 kolejnych pytaniach (jedna sesja).");
AnsiConsole.WriteLine();

// CopilotClient to obiekt SDK będący właścicielem połączenia ze środowiskiem
// wykonawczym Copilota. Traktuj go jak infrastrukturę aplikacji: utwórz go raz,
// używaj ponownie w wielu sesjach i zwolnij po zamknięciu aplikacji.
await using var client = CopilotClientFactory.Create();

// To są wywołania SDK na poziomie klienta. Sprawdzają uwierzytelnienie i dostępność
// modelu przed rozpoczęciem jakiejkolwiek rozmowy, więc CopilotSession nie jest potrzebna.
await CopilotClientFactory.GetModelsListForCopilot(client);
var modelId = CopilotClientFactory.GetModelId();

// CopilotSession to jeden wątek konwersacji. Przechowuje wybrany model,
// komunikat systemowy, zasady uprawnień, narzędzia, serwery MCP i historię tur.
// To demo celowo zadaje trzy pytania w tej samej sesji, więc SDK udostępnia
// wcześniejszy kontekst NuGet kolejnym turom.
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    // SessionConfig.Model wybiera model tej konwersacji. Wspólna fabryka
    // pozwala zmiennej COPILOT_MODEL nadpisać go bez zmiany kodu demo.
    Model = modelId,

    // Provider ma zwykle wartość null, co oznacza „użyj routingu subskrypcji GitHub
    // Copilot”. Gdy BYOK_MODE=1, fabryka zwraca ProviderConfig, więc ta sama sesja
    // używa zamiast tego punktu końcowego OpenAI/Anthropic/Azure podanego przez wywołującego.
    Provider = CopilotClientFactory.GetByokProvider(),

    // Procedury obsługi uprawnień są wywoływane przez SDK przed operacjami wymagającymi
    // zgody. To pierwsze demo nie ma niestandardowych narzędzi ani zapisów w pamięci,
    // więc „zatwierdź wszystko” utrzymuje prosty cykl życia, pokazując zarazem miejsce na hak.
    OnPermissionRequest = PermissionHandler.ApproveAll,

    // SystemMessageConfig to sposób SDK na utrwalenie instrukcji agenta dla tej sesji.
    // Tryb Append zachowuje domyślny komunikat systemowy SDK/środowiska wykonawczego
    // i dodaje do niego tę specyficzną dla demo rolę doradcy NuGet.
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Append,
        Content = """
        <role>
        You are a .NET ecosystem expert specializing in NuGet packages.
        For each package you analyze: check if it's actively maintained, suggest modern alternatives,
        and warn about known security vulnerabilities (CVE). Be concise — max 5 bullet points.
        </role>
        """,
    },
});

// session.On subskrybuje zdarzenia cyklu życia SDK. Rejestrator zdarzeń nie jest
// potrzebny do samej odpowiedzi; istnieje, aby pokazać, że każda tura Copilota jest
// strumieniem zdarzeń, a nie pojedynczym blokującym wywołaniem metody.
using var eventLog = EventLogger.Attach(session, verbose: false);

// Użycie również jest dostarczane przez zdarzenia sesji. Dołączenie tutaj reportera
// pozwala demu zbierać dane AssistantUsageEvent dla każdej tury bez zmiany
// przepływu zapytanie/odpowiedź.
var usage = new TokenUsageReporter(modelId);
using var usageSub = usage.Attach(session);

ConsoleRenderer.Rule("Sesja uruchomiona — ID: " + session.SessionId[..8] + "...");

var questions = new[]
{
    "Dzień dobry",
    "Sprawdź pakiet Newtonsoft.Json — czy warto migrować do System.Text.Json?",
    "Co konkretnie zmienia się API przy migracji? Podaj top 3 breaking changes.",
    "Które projekty .NET (Microsoft official) już używają System.Text.Json zamiast Newtonsoft?",
};

for (var i = 0; i < questions.Length; i++)
{
    AnsiConsole.WriteLine();
    ConsoleRenderer.Header($"Pytanie [{i + 1}/{questions.Length}]");
    AnsiConsole.MarkupLine($"[bold cyan]Ty:[/] {questions[i].EscapeMarkup()}");
    AnsiConsole.WriteLine();

    var answer = await ConsoleRenderer.SpinnerAsync(
        "Copilot myśli...",
        // SessionHelper.SendAndWaitAsync opakowuje sterowany zdarzeniami wzorzec SDK:
        // wywołuje session.SendAsync, aby rozpocząć turę, a następnie czeka na
        // SessionIdleEvent, które informuje nas, że środowisko wykonawcze zakończyło odpowiadać.
        () => SessionHelper.SendAndWaitAsync(session, questions[i]));

    AnsiConsole.MarkupLine("[bold green]Copilot:[/]");
    AnsiConsole.WriteLine(answer);
    usage.RenderLastTurn();
    ConsoleRenderer.Rule();
}

AnsiConsole.WriteLine();
ConsoleRenderer.Header("Podsumowanie zużycia tokenów");
usage.RenderTotals();


ConsoleRenderer.Success("Demo zakończone. Jedna sesja, trzy pytania — model zachował kontekst.");
ConsoleRenderer.Info($"Session ID: {session.SessionId}");

/// Reporter zdarzeń użycia tokenów emitowanych przez SDK. AssistantUsageEvent.Data
/// zawiera modelowe rozliczenie zakończonej tury, w tym tokeny zapytania,
/// tokeny odpowiedzi, odczyty/zapisy cache oraz tokeny rozumowania tam, gdzie
/// obsługuje je wybrany model.
sealed class TokenUsageReporter
{
    private readonly string _modelId;

    private long _lastInput, _lastCacheRead, _lastCacheWrite, _lastReasoning, _lastOutput;
    private long _sumInput, _sumCacheRead, _sumCacheWrite, _sumReasoning, _sumOutput;
    private bool _hasLast;

    public TokenUsageReporter(string? modelId = null)
    {
        _modelId = modelId ?? "unknown";
    }

    public IDisposable Attach(CopilotSession session) =>
        // Subskrybuj wszystkie zdarzenia sesji SDK i filtruj wewnątrz Handle. Dzięki
        // temu reporter pozostaje odporny, gdy zdarzenia użycia są przeplatane
        // wiadomościami asystenta, zdarzeniami narzędzi lub zdarzeniami bezczynności.
        session.On<SessionEvent>(Handle);

    private void Handle(SessionEvent evt)
    {
        if (evt is not AssistantUsageEvent { Data: { } d })
            return;

        // AssistantUsageEvent jest emitowane na turę. SDK może pominąć poszczególne pola
        // tokenów w zależności od obsługi dostawcy/modelu, więc null oznacza „nie
        // raportowano” i jest wyświetlane jako zero dla czytelności demo.
        _lastInput      = d.InputTokens      ?? 0;
        _lastCacheRead  = d.CacheReadTokens  ?? 0;
        _lastCacheWrite = d.CacheWriteTokens ?? 0;
        _lastReasoning  = d.ReasoningTokens  ?? 0;
        _lastOutput     = d.OutputTokens     ?? 0;
        _hasLast        = true;

        _sumInput      += _lastInput;
        _sumCacheRead  += _lastCacheRead;
        _sumCacheWrite += _lastCacheWrite;
        _sumReasoning  += _lastReasoning;
        _sumOutput     += _lastOutput;
    }

    public void RenderLastTurn()
    {
        if (!_hasLast)
        {
            ConsoleRenderer.Info("Brak danych o tokenach dla tej tury (SDK nie zgłosił usage).");
            return;
        }

        var total = _lastInput + _lastOutput;
        AnsiConsole.MarkupLine(
            $"[grey]Tokeny (tura):[/] " +
            $"wejście=[yellow]{_lastInput}[/] " +
            $"(cacheR=[green]{_lastCacheRead}[/] cacheW=[green]{_lastCacheWrite}[/]) · " +
            $"rozumowanie=[cyan]{_lastReasoning}[/] · " +
            $"wyjście=[yellow]{_lastOutput}[/] · razem=[bold yellow]{total}[/]");

        _hasLast = false;
    }

    public void RenderTotals()
    {
        var tokenTable = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]Tokeny — model: {_modelId}[/]");
        tokenTable.AddColumn("Metryka");
        tokenTable.AddColumn(new TableColumn("Tokeny").RightAligned());
        tokenTable.AddRow("Wejście (prompt)",        _sumInput.ToString("N0"));
        tokenTable.AddRow("  cache read",            _sumCacheRead.ToString("N0"));
        tokenTable.AddRow("  cache write",           _sumCacheWrite.ToString("N0"));
        tokenTable.AddRow("Rozumowanie (reasoning)", _sumReasoning.ToString("N0"));
        tokenTable.AddRow("Wyjście (completion)",    _sumOutput.ToString("N0"));
        tokenTable.AddRow("[bold]Razem (in+out)[/]", $"[bold]{_sumInput + _sumOutput:N0}[/]");
        AnsiConsole.Write(tokenTable);
    }
}
