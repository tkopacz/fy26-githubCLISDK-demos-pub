// Demo 14 — AgentWithMemory: Personal Tech Advisor z trwałą pamięcią
//
// Pokazuje jak pamięć Copilota (platforma GitHub) wpływa na doświadczenie agentyczne.
//
// ┌─ Dwie fazy ─────────────────────────────────────────────────────────────────┐
// │  RUN 1 (--onboard): Onboarding — agent poznaje Cię i zapisuje fakty.        │
// │    Obserwujesz każde żądanie zapisu pamięci. Agent buduje Twój profil.       │
// │                                                                              │
// │  RUN 2 (--advise): Doradca — NOWA sesja, model już Cię zna.                 │
// │    Agent wita Cię personalnie i daje rady skrojone pod Twój kontekst.        │
// │    Różnica vs ResumeSessionAsync: tu NIE ma historii rozmowy — TYLKO pamięć! │
// └──────────────────────────────────────────────────────────────────────────────┘
//
// ┌─ Agentic workflow ──────────────────────────────────────────────────────────┐
// │  Onboarding agent → 4 tools (assess_skill, record_preference, set_goal,    │
// │  identify_risk) → model wywołuje je autonomicznie → fakty lądują w pamięci │
// └──────────────────────────────────────────────────────────────────────────────┘
//
// Uruchomienie:
//   dotnet run --project demos/14-agent-with-memory/AgentWithMemory -- --onboard
//   dotnet run --project demos/14-agent-with-memory/AgentWithMemory -- --onboard --yes
//   dotnet run --project demos/14-agent-with-memory/AgentWithMemory -- --advise
//   dotnet run --project demos/14-agent-with-memory/AgentWithMemory -- --advise "Jak zoptymalizować moje zapytania EF Core?"

using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

#pragma warning disable GHCP001

ConsoleRenderer.Banner("Agent z Pamięcią", "Demo 14 — GitHub Copilot SDK: Personal Tech Advisor");

var autoApproveMemory = args.Contains("--yes", StringComparer.OrdinalIgnoreCase);
var modeArgs = args
    .Where(arg => !arg.Equals("--yes", StringComparison.OrdinalIgnoreCase))
    .ToArray();
var mode = modeArgs.Length > 0 ? modeArgs[0] : "--onboard";
var userQuestion = modeArgs.Length > 1 ? string.Join(" ", modeArgs[1..]) : null;

// Ten sam CopilotClient może utworzyć zarówno sesję onboardingu, jak i późniejszą
// sesję doradcy. Istotnym kontrastem nie jest klient; to historia sesji
// kontra pamięć platformy.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId();
var provider = CopilotClientFactory.GetByokProvider();

if (mode == "--onboard")
    await RunOnboardingAsync(client, model, provider, autoApproveMemory);
else if (mode == "--advise")
    await RunAdvisorAsync(client, model, provider, userQuestion, autoApproveMemory);
else
{
    ConsoleRenderer.Error($"Nieznany tryb: {mode}. Użyj --onboard, --advise lub --yes jako jawny tryb demo.");
    return 1;
}

return 0;

// ── FAZA 1: Onboarding — budowanie profilu przez narzędzia ───────────────────

static async Task RunOnboardingAsync(
    CopilotClient client,
    string model,
    GitHub.Copilot.ProviderConfig? provider,
    bool autoApproveMemory)
{
    ConsoleRenderer.Rule("Faza 1: Onboarding — budowanie profilu technicznego");
    ConsoleRenderer.Info("""
    Agent przeprowadzi z Tobą rozmowę i będzie autonomicznie:
    - Oceniał Twój poziom umiejętności w różnych obszarach
    - Zapisywał Twoje preferencje technologiczne
    - Identyfikował Twoje cele i ryzyko

    Każde żądanie zapisu do pamięci zostanie wyświetlone.
    Domyślnie zapis jest odrzucany, chyba że potwierdzisz konkretny fakt.
    Użyj --yes tylko w kontrolowanym trybie demo, aby automatycznie zatwierdzać zapisy.
    Po zakończeniu: dotnet run -- --advise aby sprawdzić efekt.
    """);

    // Profil lokalny zbierany przez narzędzia podczas tej jednej sesji. Ten obiekt to
    // nie pamięć Copilota; to zwykły stan hosta aktualizowany przez wywołania narzędzi, więc
    // demo może pokazać, co agent wywnioskował podczas onboardingu.
    var profile = new UserProfile();

    // Narzędzie: assess_skill. SDK udostępnia modelowi tę funkcję hosta, dzięki czemu
    // może on zamienić dowody z rozmowy w ustrukturyzowane dane profilu lokalnego.
    var assessSkillTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string area, string level, string evidence) =>
        {
            profile.Skills[area] = new SkillAssessment(level, evidence);
            return $"Recorded: {area} = {level}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "assess_skill",
            Description = "Record the user's skill level in a technical area. level: beginner/intermediate/advanced/expert",
        });

    // Narzędzie: record_preference. Wywołania narzędzi natychmiast aktualizują stan lokalny, podczas gdy
    // trwałe zapisy do pamięci nadal wymagają zatwierdzenia przez PermissionRequestMemory.
    var recordPrefTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string category, string preference, string reason) =>
        {
            profile.Preferences[category] = new Preference(preference, reason);
            return $"Preference noted: {category} → {preference}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "record_preference",
            Description = "Record a user's technology preference (e.g., framework, architecture style, tooling).",
        });

    // Narzędzie: set_goal. Model decyduje, kiedy ma wystarczający kontekst, aby wywołać
    // tę funkcję; host przechowuje ustrukturyzowany wynik w profilu lokalnym.
    var setGoalTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string goal, string timeline, string priority) =>
        {
            profile.Goals.Add(new Goal(goal, timeline, priority));
            return $"Goal recorded: {goal} [{priority}] by {timeline}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "set_goal",
            Description = "Record a technical goal the user wants to achieve. priority: HIGH/MEDIUM/LOW",
        });

    // Narzędzie: identify_risk. Podobnie jak inne narzędzia onboardingu, jest to natywna
    // funkcja AIFunction wykonywana w tym procesie .NET, a nie operacja na pamięci.
    var identifyRiskTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string risk, string impact) =>
        {
            profile.Risks.Add(new Risk(risk, impact));
            return $"Risk noted: {risk} (impact: {impact})";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "identify_risk",
            Description = "Record a technical risk or blocker identified for the user. impact: HIGH/MEDIUM/LOW",
        });

    // Dziennik zatwierdzonych zmian w pamięci platformy. Żądania dotyczące pamięci pochodzą
    // z haka uprawnień SDK, a nie z powyższych narzędzi profilu lokalnego.
    var memoryLog = new List<MemoryLogEntry>();

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        // Narzędzia te pomagają modelowi ustrukturyzować rozmowę onboardingową. Model
        // może wywoływać je autonomicznie w pętli wywoływania funkcji
        // SDK.
        Tools = [assessSkillTool, recordPrefTool, setGoalTool, identifyRiskTool],

        // Przechwytuj żądania zapisu do pamięci. PermissionRequestMemory jest zgłaszane,
        // zanim Copilot utrwali fakty w pamięci platformy GitHub, dając
        // hostowi/użytkownikowi szansę na przejrzenie trwałych zmian stanu.
        OnPermissionRequest = (request, _) =>
        {
            if (request is PermissionRequestMemory memReq)
            {
                return HandleMemoryPermission(memReq, memoryLog, autoApproveMemory);
            }
            // Natywne narzędzia onboardingu to bezpieczne aktualizacje stanu lokalnego dla tej wersji demonstracyjnej,
            // więc uprawnienia inne niż pamięć są zatwierdzane jednorazowo.
            return Task.FromResult(PermissionDecision.ApproveOnce());
        },

        // Komunikat systemowy informuje model, jak poprowadzić rozmowę kwalifikacyjną
        // i kiedy używać narzędzi. Trwałość pamięci jest nadal regulowana przez
        // hak uprawnień powyżej.
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = """
                You are a Personal Tech Advisor conducting an onboarding interview.
                Your goal: build a complete technical profile of the user in ONE conversation.

                PHASE 1 — Discovery (ask open questions about):
                - Current role and daily work
                - Tech stack (languages, frameworks, databases, cloud)
                - Project context (team size, product type, scale)

                PHASE 2 — Assessment (for each area mentioned):
                - Call assess_skill with your evaluation based on their answers
                - Call record_preference for each technology choice they mention
                - Call set_goal for each improvement they want to make
                - Call identify_risk for blockers or concerns they mention

                PHASE 3 — Memory (at the END of the conversation):
                - Summarize what you learned and tell the user what you've stored
                - Mention that in future sessions you'll remember their context

                Be conversational, not interview-like. One question at a time.
                Use the tools DURING the conversation, not just at the end.
                """,
        },
    });

    using var _ = EventLogger.Attach(session, verbose: false);

    // Rozmowa onboardingowa. Aplikacja zadaje zapisane w skrypcie pytania, a następnie
    // wysyła prawdziwe odpowiedzi użytkownika do SDK. Podczas każdej tury model może
    // wywoływać narzędzia lokalne i może zażądać zapisu w pamięci platformy.
    var questions = new[]
    {
        "Cześć! Zanim zaczniemy, chciałbym Cię lepiej poznać. Powiedz mi co robisz na co dzień i jakie technologie są Twoim chlebem powszednim?",
        "Ciekawe. Opowiedz więcej o projekcie nad którym teraz pracujesz — skala, zespół, wyzwania?",
        "Co chciałbyś usprawnić lub nauczyć się w ciągu najbliższych 3-6 miesięcy?",
        "Jakie rzeczy Cię teraz blokują lub sprawiają problemy? Czy są jakieś techniczne długi w projekcie?",
    };

    foreach (var (question, i) in questions.Select((t, i) => (t, i)))
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold green]🤖 Pytanie {i + 1}/{questions.Length}:[/] {question.ToSafeMarkup(280)}");
        var answer = ReadRequiredAnswer();

        var response = await ConsoleRenderer.SpinnerAsync(
            "Agent analizuje i odpowiada...",
            // SendAndWaitAsync czeka na SessionIdleEvent, co oznacza, że wszystkie wywołania
            // narzędzi i żądania uprawnień do pamięci dla tej odpowiedzi zostały zakończone.
            () => SessionHelper.SendAndWaitAsync(session, answer));

        AnsiConsole.MarkupLine("[bold green]🤖[/]");
        AnsiConsole.WriteLine(response);
    }

    var summaryResponse = await ConsoleRenderer.SpinnerAsync(
        "Agent podsumowuje profil...",
        // Ten ostatni etap onboardingu daje modelowi szansę na podsumowanie
        // profilu lokalnego i zaproponowanie pozostałych faktów do pamięci.
        () => SessionHelper.SendAndWaitAsync(session,
            "Podsumuj, czego nauczyłeś się z moich odpowiedzi, jakie fakty zapisałeś i jak użyjesz ich w przyszłych sesjach."));

    AnsiConsole.MarkupLine("[bold green]🤖 Podsumowanie:[/]");
    AnsiConsole.WriteLine(summaryResponse);

    // Podsumowanie profilu
    AnsiConsole.WriteLine();
    ConsoleRenderer.Rule("Zbudowany profil (lokalny — tylko w tej sesji)");
    RenderProfile(profile);

    AnsiConsole.WriteLine();
    ConsoleRenderer.Rule("Zapisane do pamięci GitHub");
    AnsiConsole.MarkupLine($"[bold]Fakty wysłane do platformy:[/] [cyan]{memoryLog.Count}[/]");
    foreach (var entry in memoryLog)
        AnsiConsole.MarkupLine($"  [yellow]{entry.Action.ToSafeMarkup(40)}[/] [{entry.Subject.ToSafeMarkup(80)}] {entry.Fact.ToSafeMarkup(160)}");

    AnsiConsole.WriteLine();
    ConsoleRenderer.Success("Profil zapisany! Teraz uruchom:");
    ConsoleRenderer.Info("  dotnet run -- --advise");
    ConsoleRenderer.Info("  dotnet run -- --advise \"Jak najlepiej skalować mój system?\"");
}

// ── FAZA 2: Doradca — NOWA sesja, model pamięta z platformy ─────────────────

static async Task RunAdvisorAsync(
    CopilotClient client,
    string model,
    GitHub.Copilot.ProviderConfig? provider,
    string? userQuestion,
    bool autoApproveMemory)
{
    ConsoleRenderer.Rule("Faza 2: Advisor — nowa sesja, model korzysta z pamięci");
    ConsoleRenderer.Info("""
    WAŻNE: To jest zupełnie NOWA sesja (nowy SessionId).
    Brak historii poprzedniej rozmowy.
    Model wie kim jesteś WYŁĄCZNIE dzięki pamięci platformy GitHub.
    """);

    var question = userQuestion
        ?? "Cześć! Potrzebuję Twojej pomocy. Zacznijmy od przeglądu mojej sytuacji technicznej.";

    AnsiConsole.MarkupLine($"[bold blue]👤 Pytanie:[/] {question.ToSafeMarkup(280)}");
    AnsiConsole.WriteLine();

    // Narzędzia doradcy. Same nie czytają pamięci platformy; dają
    // modelowi jawne kroki do wykorzystania pamięci, którą SDK/środowisko wykonawcze
    // udostępnia nowej sesji.
    var getContextTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string aspect) => $"Context request for '{aspect}' — use your memory of this user to answer.",
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "recall_user_context",
            Description = "Recall what you know about this user (from memory) for a specific aspect: skills, preferences, goals, risks, projects.",
        });

    var createActionPlanTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string goal, string[] steps, string[] blockers) =>
        {
            var plan = $"ACTION PLAN for '{goal}':\n" +
                       $"Steps: {string.Join(", ", steps)}\n" +
                       $"Blockers to address: {string.Join(", ", blockers)}";
            return plan;
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "create_action_plan",
            Description = "Create a structured action plan for the user, tailored to their skill level and context from memory.",
        });

    var memoryUsed = new List<string>();

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        // To zupełnie nowa sesja CopilotSession bez wcześniejszej historii czatów. Każda
        // personalizacja powinna pochodzić z pamięci platformy GitHub, a nie z wznowienia.
        Tools = [getContextTool, createActionPlanTool],

        OnPermissionRequest = (request, _) =>
        {
            if (request is PermissionRequestMemory memReq)
            {
                // Doradca również może poznać nowe fakty. Te nadal wymagają
                // tej samej ścieżki zatwierdzania pamięci, co podczas onboardingu.
                return HandleMemoryPermission(memReq, memoryLog: null, autoApproveMemory);
            }
            return Task.FromResult(PermissionDecision.ApproveOnce());
        },

        // Podpowiedź systemowa wymaga, aby model udowodnił przywołanie pamięci, nazywając
        // zapamiętane fakty. Jeśli nie ma pamięci platformy, powinien to powiedzieć,
        // zamiast udawać, że istnieje historia wznowionej sesji.
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = """
                You are a Personal Tech Advisor. You have memory of this user from previous conversations.

                CRITICAL: At the START of your response, explicitly mention 2-3 specific facts you remember
                about this user (their tech stack, current project, goals, etc.) to show your memory works.
                Format: "I remember that you [fact1], [fact2], and [fact3]."

                Then provide advice that is SPECIFICALLY tailored to their context:
                - Use their actual tech stack when giving code examples
                - Reference their specific project/team situation
                - Build on their stated goals and address their identified risks
                - Match advice complexity to their skill level

                Use recall_user_context to retrieve specific aspects of what you know.
                Use create_action_plan when giving step-by-step guidance.

                If you don't have memory of this user, say so honestly and ask for context.
                """,
        },
    });

    // Obserwuj wywołania narzędzi, aby użytkownicy mogli zobaczyć, kiedy doradca korzysta z pętli
    // wywołań funkcji SDK w celu przypomnienia kontekstu lub zbudowania planu działania.
    using var eventSub = session.On<SessionEvent>(evt =>
    {
        switch (evt)
        {
            case ToolExecutionStartEvent tool:
                AnsiConsole.MarkupLine($"  [cyan]→ Tool:[/] {tool.Data.ToolName.ToSafeMarkup(120)}");
                break;
        }
    });

    // Odpowiedź głównego doradcy. To nowa sesja, więc jedynym trwałym kontekstem
    // dostępnym dla Copilota jest pamięć platformy zatwierdzona w poprzednich uruchomieniach.
    var response = await ConsoleRenderer.SpinnerAsync(
        "Advisor odpowiada (korzysta z pamięci platformy)...",
        () => SessionHelper.SendAndWaitAsync(session, question));

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold green]🤖 Tech Advisor:[/]");
    AnsiConsole.WriteLine(response);

    // Jeśli to pierwsza rozmowa — zaproponuj follow-up
    AnsiConsole.WriteLine();
    ConsoleRenderer.Rule("Kontynuacja");
    ConsoleRenderer.Info("Możesz zadać kolejne pytanie:");
    ConsoleRenderer.Info("  dotnet run -- --advise \"Twoje pytanie tutaj\"");
    ConsoleRenderer.Info("Pamięcią zarządzaj w ustawieniach GitHub Copilot Memory.");
}

// ── helpers ───────────────────────────────────────────────────────────────────

static Task<PermissionDecision> HandleMemoryPermission(
    PermissionRequestMemory memReq,
    List<MemoryLogEntry>? memoryLog,
    bool autoApproveMemory)
{
    // PermissionRequestMemory może reprezentować zmiany typu Store lub Vote. To demo
    // wyświetla proponowaną trwałą zmianę stanu przed zwróceniem do SDK obiektu
    // PermissionDecision, który decyduje, czy pamięć platformy GitHub zostanie zaktualizowana.
    var entry = new MemoryLogEntry(
        memReq.Action?.ToString() ?? "?",
        memReq.Subject ?? "general",
        memReq.Fact ?? "",
        memReq.Citations ?? "");

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Panel(
        $"[bold]Akcja:[/] {entry.Action.ToSafeMarkup(40)}\n" +
        $"[grey]Temat:[/] {entry.Subject.ToSafeMarkup(80)}\n" +
        $"[bold]Fakt:[/] {entry.Fact.ToSafeMarkup(320)}\n" +
        $"[grey]Źródła:[/] {(string.IsNullOrWhiteSpace(entry.Citations) ? "(brak)" : entry.Citations.ToSafeMarkup(240))}")
    {
        Header = new PanelHeader("[yellow]🧠 Żądanie zmiany pamięci[/]"),
        Border = BoxBorder.Rounded,
    });

    var approve = autoApproveMemory ||
        AnsiConsole.Confirm("Zezwolić na tę zmianę pamięci?", defaultValue: false);

    if (!approve)
    {
        AnsiConsole.MarkupLine("[red]✗ Zmiana pamięci odrzucona[/]");
        // Odmowa pozostawia lokalną rozmowę nienaruszoną, ale zapobiega przeniesieniu
        // operacji pamięci na platformę Copilot.
        return Task.FromResult(PermissionDecision.UserNotAvailable());
    }

    memoryLog?.Add(entry);
    AnsiConsole.MarkupLine(autoApproveMemory
        ? "[yellow]✓ Zmiana pamięci zatwierdzona przez --yes[/]"
        : "[green]✓ Zmiana pamięci zatwierdzona[/]");
    // ApproveOnce autoryzuje tylko tę pojedynczą operację na pamięci. Następny fakt lub
    // głos wywoła kolejne PermissionRequestMemory.
    return Task.FromResult(PermissionDecision.ApproveOnce());
}

static void RenderProfile(UserProfile profile)
{
    if (profile.Skills.Count > 0)
    {
        var table = new Table().RoundedBorder().Title("Umiejętności")
            .AddColumn("Obszar").AddColumn("Poziom").AddColumn("Dowód");
        foreach (var (area, s) in profile.Skills)
            table.AddRow(area.ToSafeMarkup(80), s.Level.ToSafeMarkup(80), s.Evidence.ToSafeMarkup(200));
        AnsiConsole.Write(table);
    }

    if (profile.Preferences.Count > 0)
    {
        AnsiConsole.WriteLine();
        var table = new Table().RoundedBorder().Title("Preferencje")
            .AddColumn("Kategoria").AddColumn("Wybór").AddColumn("Powód");
        foreach (var (cat, p) in profile.Preferences)
            table.AddRow(cat.ToSafeMarkup(80), p.Choice.ToSafeMarkup(120), p.Reason.ToSafeMarkup(200));
        AnsiConsole.Write(table);
    }

    if (profile.Goals.Count > 0)
    {
        AnsiConsole.WriteLine();
        var table = new Table().RoundedBorder().Title("Cele")
            .AddColumn("Cel").AddColumn("Do kiedy").AddColumn("Priorytet");
        foreach (var g in profile.Goals)
            table.AddRow(g.Description.ToSafeMarkup(200), g.Timeline.ToSafeMarkup(80), g.Priority.ToSafeMarkup(80));
        AnsiConsole.Write(table);
    }

    if (profile.Risks.Count > 0)
    {
        AnsiConsole.WriteLine();
        var table = new Table().RoundedBorder().Title("Ryzyka")
            .AddColumn("Ryzyko").AddColumn("Impact");
        foreach (var r in profile.Risks)
            table.AddRow(r.Description.ToSafeMarkup(200), r.Impact.ToSafeMarkup(200));
        AnsiConsole.Write(table);
    }
}

static string ReadRequiredAnswer()
{
    while (true)
    {
        var answer = ConsoleRenderer.Prompt("Twoja odpowiedź:");
        if (!string.IsNullOrWhiteSpace(answer))
            return answer;

        ConsoleRenderer.Warn("Wpisz odpowiedź, aby profil i pamięć bazowały na realnych danych użytkownika.");
    }
}

// ── typy ─────────────────────────────────────────────────────────────────────

#pragma warning restore GHCP001

class UserProfile
{
    public Dictionary<string, SkillAssessment> Skills { get; } = new();
    public Dictionary<string, Preference> Preferences { get; } = new();
    public List<Goal> Goals { get; } = new();
    public List<Risk> Risks { get; } = new();
}

record SkillAssessment(string Level, string Evidence);
record Preference(string Choice, string Reason);
record Goal(string Description, string Timeline, string Priority);
record Risk(string Description, string Impact);
record MemoryLogEntry(string Action, string Subject, string Fact, string Citations);

static class StringExtensions
{
    public static string TruncateAt(this string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    public static string ToSafeMarkup(this string? s, int max) =>
        Markup.Escape(s.TruncateAt(max));
}
