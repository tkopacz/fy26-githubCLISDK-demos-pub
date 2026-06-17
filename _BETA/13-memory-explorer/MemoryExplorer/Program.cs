// Demo 13 — MemoryExplorer: Pamięć platformy GitHub Copilot
//
// Pokazuje jak przechwycić i kontrolować momenty gdy model chce zapisać
// fakt o użytkowniku na platformie GitHub (PermissionRequestMemory).
//
// Dwie akcje modelu:
//   Store — model chce zapamiętać nowy fakt (Subject + Fact + Citations)
//   Vote  — model chce podbić/obniżyć wagę istniejącego faktu (Upvote/Downvote)
//
// Fakty zapisane przez SDK są trwałe i dostępne we WSZYSTKICH sesjach
// (na różnych maszynach) — to jest "platform memory", nie session memory.
//
// Uruchomienie:
//   dotnet run --project demos/13-memory-explorer/MemoryExplorer

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

#pragma warning disable GHCP001

ConsoleRenderer.Banner("Memory Explorer", "Demo 13 — GitHub Copilot SDK: Platform Memory System");

ConsoleRenderer.Info("""
Konwersacja z modelem, który aktywnie zapamiętuje fakty o użytkowniku.
Każda próba zapisania faktu zostanie przechwycona — możesz zatwierdzić lub odrzucić.
Zatwierdzone fakty są zapisywane trwale na platformie GitHub przez Copilot CLI.
""");

await ExploreMemoryAsync();
return 0;

// ── Sesja z interceptowaniem memory permissions ───────────────────────────────

static async Task ExploreMemoryAsync()
{
    // Pamięcią platformy zarządza środowisko wykonawcze Copilot SDK, ale aplikacja hosta
    // nadal tworzy normalnego klienta/sesję. Żądania dotyczące pamięci pojawiają się jako
    // żądania uprawnień w trakcie rozmowy.
    await using var client = CopilotClientFactory.Create();
    var model = CopilotClientFactory.GetModelId();
    var provider = CopilotClientFactory.GetByokProvider();

    var approvedFacts = new List<StoredFact>();
    var rejectedFacts = new List<StoredFact>();
    var voteEvents = new List<VoteEvent>();

    ConsoleRenderer.Rule("Sesja z GitHub Copilot Memory");

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,

        // Ten hak uprawnień jest ważną częścią tej wersji demonstracyjnej. Większość dem
        // zatwierdza wszystkie uprawnienia, ale zapisy do pamięci są trwałe między sesjami
        // i maszynami, więc host jawnie sprawdza PermissionRequestMemory.
        OnPermissionRequest = (request, _) =>
        {
            if (request is PermissionRequestMemory memReq)
                return HandleMemoryPermissionAsync(memReq, approvedFacts, rejectedFacts, voteEvents);

            // Żądania uprawnień inne niż pamięć nie są tematem tego demo. Zatwierdzenie
            // ich sprawia, że demonstracja koncentruje się na decyzjach o zapisie/głosowaniu w pamięci.
            return Task.FromResult(PermissionDecision.ApproveOnce());
        },

        // Komunikat systemowy zachęca model do wykorzystania jego możliwości pamięci.
        // SDK nie będzie po cichu utrwalać faktów: każda próba zapisu lub głosowania
        // nadal przechodzi przez OnPermissionRequest powyżej.
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = """
                You are a helpful technical assistant who is getting to know the user.

                IMPORTANT: Actively use your memory tools to store facts about the user as you learn them.
                When the user mentions:
                - Their programming languages, frameworks, or tech stack → store it
                - Their current projects or work context → store it
                - Their preferences (coding style, architecture choices) → store it
                - Their team size, company type, or domain → store it
                - Any constraints or requirements they mention → store it

                Be conversational. Ask follow-up questions to learn more.
                After each user message, try to identify at least one fact worth remembering.
                """,
        },
    });

    var questions = new[]
    {
        "Cześć! Chciałbym Cię lepiej poznać. Jakich języków programowania i frameworków używasz na co dzień?",
        "Nad czym teraz pracujesz? Jaki jest kontekst projektu?",
        "Jakie masz preferencje jeśli chodzi o architekturę? Monolity, mikroserwisy, coś innego?",
        "A co z testami — jakiej strategii używasz? Unit, integration, e2e?",
    };

    foreach (var question in questions)
    {
        AnsiConsole.MarkupLine($"\n[bold green]🤖 Pytanie:[/] {SafeMarkup(question, 240)}");
        AnsiConsole.WriteLine();
        var answer = ReadRequiredAnswer();

        var response = await ConsoleRenderer.SpinnerAsync(
            "Model odpowiada...",
            // Każda odpowiedź użytkownika jest wysyłana jako zwykła tura SDK. Jeśli model zdecyduje,
            // że należy coś zapamiętać, wywoływane jest PermissionRequestMemory
            // przed zatwierdzeniem operacji na pamięci platformy.
            () => SessionHelper.SendAndWaitAsync(session, answer));

        AnsiConsole.MarkupLine("[bold green]🤖 Odpowiedź:[/]");
        AnsiConsole.WriteLine(response);
        AnsiConsole.WriteLine();
    }

    AnsiConsole.WriteLine();
    ConsoleRenderer.Rule("Podsumowanie sesji pamięci");
    ShowMemorySummary(approvedFacts, rejectedFacts, voteEvents);
}

// ── Obsługa żądania zapisu memory ────────────────────────────────────────────

static Task<PermissionDecision> HandleMemoryPermissionAsync(
    PermissionRequestMemory memReq,
    List<StoredFact> approved,
    List<StoredFact> rejected,
    List<VoteEvent> votes)
{
    AnsiConsole.WriteLine();

    if (memReq.Action == PermissionRequestMemoryAction.Store)
    {
        // Store oznacza, że model proponuje nowy trwały fakt w pamięci. Temat,
        // fakt i cytaty są generowane przez model i powinny zostać przejrzane
        // przez hosta/użytkownika przed zatwierdzeniem trwałego zapisu w pamięci
        // platformy GitHub.
        var citations = !string.IsNullOrWhiteSpace(memReq.Citations)
            ? $"[grey]Źródła:[/] {SafeMarkup(memReq.Citations, 240)}"
            : "";
        var panel = new Panel(
            $"[bold]Fakt:[/] {SafeMarkup(memReq.Fact, 320)}\n" +
            $"[grey]Temat:[/] {SafeMarkup(memReq.Subject ?? "(brak)", 80)}\n" +
            citations)
        {
            Header = new PanelHeader("[yellow]🧠 Model chce zapamiętać fakt[/]"),
            Border = BoxBorder.Rounded,
        };
        AnsiConsole.Write(panel);

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Co robimy z tym faktem?")
                .AddChoices("✅ Zatwierdź — zapisz na GitHubie", "❌ Odrzuć — nie zapisuj"));

        if (choice.StartsWith("✅"))
        {
            approved.Add(new StoredFact(memReq.Subject ?? "?", memReq.Fact ?? "?"));
            AnsiConsole.MarkupLine("[green]✓ Fakt zatwierdzony[/]");
            // ApproveOnce zezwala tylko na ten jeden zapis do pamięci. Przyszłe fakty wywołają
            // własne PermissionRequestMemory i wymagają osobnej decyzji.
            return Task.FromResult(PermissionDecision.ApproveOnce());
        }
        else
        {
            rejected.Add(new StoredFact(memReq.Subject ?? "?", memReq.Fact ?? "?"));
            AnsiConsole.MarkupLine("[red]✗ Fakt odrzucony[/]");
            // UserNotAvailable odrzuca operację na pamięci; proponowany fakt
            // nie zostaje zapisany w pamięci platformy.
            return Task.FromResult(PermissionDecision.UserNotAvailable());
        }
    }

    if (memReq.Action == PermissionRequestMemoryAction.Vote)
    {
        // Głosowanie oznacza, że model chce dostosować pewność/wagę istniejącego faktu
        // w pamięci. To wciąż trwały stan platformy, więc demo pyta o zgodę przed
        // zatwierdzeniem upvote/downvote.
        var direction = memReq.Direction == PermissionRequestMemoryDirection.Upvote ? "⬆️ Upvote" : "⬇️ Downvote";
        AnsiConsole.MarkupLine($"[yellow]🗳️ Model głosuje[/] {SafeMarkup(direction, 40)} [yellow]na fakt:[/]");
        AnsiConsole.MarkupLine($"  Fakt: {SafeMarkup(memReq.Fact, 320)}");
        AnsiConsole.MarkupLine($"  Powód: {SafeMarkup(memReq.Reason ?? "(brak)", 240)}");

        votes.Add(new VoteEvent(memReq.Fact ?? "?", memReq.Direction?.ToString() ?? "?", memReq.Reason ?? ""));

        var allow = AnsiConsole.Confirm("Zezwolić na głosowanie?", defaultValue: true);
        return Task.FromResult(allow ? PermissionDecision.ApproveOnce() : PermissionDecision.UserNotAvailable());
    }

    return Task.FromResult(PermissionDecision.ApproveOnce());
}

// ── helpers ───────────────────────────────────────────────────────────────────

static void ShowMemorySummary(List<StoredFact> approved, List<StoredFact> rejected, List<VoteEvent> votes)
{
    AnsiConsole.MarkupLine($"[bold]Zatwierdzone fakty:[/] [green]{approved.Count}[/]");
    foreach (var f in approved)
        AnsiConsole.MarkupLine($"  [green]✓[/] [{SafeMarkup(f.Subject, 80)}] {SafeMarkup(f.Fact, 240)}");

    if (rejected.Count > 0)
    {
        AnsiConsole.MarkupLine($"\n[bold]Odrzucone fakty:[/] [red]{rejected.Count}[/]");
        foreach (var f in rejected)
            AnsiConsole.MarkupLine($"  [red]✗[/] [{SafeMarkup(f.Subject, 80)}] {SafeMarkup(f.Fact, 240)}");
    }

    if (votes.Count > 0)
    {
        AnsiConsole.MarkupLine($"\n[bold]Głosowania:[/] {votes.Count}");
        foreach (var v in votes)
            AnsiConsole.MarkupLine($"  {(v.Direction == "Upvote" ? "⬆️" : "⬇️")} {SafeMarkup(v.Fact, 240)}");
    }
}

static string SafeMarkup(string? value, int maxChars) =>
    Markup.Escape(value.TruncateAt(maxChars));

static string ReadRequiredAnswer()
{
    while (true)
    {
        var answer = ConsoleRenderer.Prompt("Twoja odpowiedź:");
        if (!string.IsNullOrWhiteSpace(answer))
            return answer;

        ConsoleRenderer.Warn("Wpisz odpowiedź, aby model zapamiętywał fakty z realnej rozmowy.");
    }
}

// ── typy ─────────────────────────────────────────────────────────────────────

#pragma warning restore GHCP001

record StoredFact(string Subject, string Fact);
record VoteEvent(string Fact, string Direction, string Reason);

static class StringExtensions
{
    public static string TruncateAt(this string? value, int maxChars) =>
        value is null ? "" : value.Length <= maxChars ? value : value[..maxChars] + "…";
}
