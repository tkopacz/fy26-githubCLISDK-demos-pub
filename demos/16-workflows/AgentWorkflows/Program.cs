using CopilotSDK.Demos.Demos.AgentWorkflows;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using GitHub.Copilot;
using Spectre.Console;

ConsoleRenderer.Banner("Agent Workflows", "Demo 16 — SDLC pipeline with standard Copilot agents");
ConsoleRenderer.Info("3-etapowy pipeline SDLC. Etap 2 uruchamia subagentów z LLM oraz równoległą analizę deterministyczną.\n");

var projectRoot = AgentWorkflowPaths.ResolveSampleProjectRoot(AppContext.BaseDirectory, args);
// Obiekty CustomAgentConfig są ładowane z plików markdown w .github/agents,
// a następnie przekazywane bezpośrednio do SessionConfig. Dzięki temu SDK może
// uruchamiać nazwanych agentów bez polegania na globalnym stanie maszyny.
var customAgents = LoadCustomAgents(Path.Combine(projectRoot, ".github", "agents"));

ConsoleRenderer.Info($"Załadowano custom agents: {string.Join(", ", customAgents.Select(agent => agent.Name))}");

// Jeden klient SDK jest właścicielem połączenia wykonawczego dla całego przepływu pracy.
// Każdy etap tworzy nową sesję CopilotSession z inną wartością Agent.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId("gpt-5.5"
    /*"claude-sonnet-4.6"*/ /*"gpt-5.5"*/);
var provider = CopilotClientFactory.GetByokProvider();

ConsoleRenderer.Rule("Etap 1 — requirements-analyst");
var stage1 = await RunAgentStageAsync(
    client,
    model,
    provider,
    projectRoot,
    customAgents,
    "requirements-analyst",
    $"""
    Analyze the SDLC input in {projectRoot}.
    Produce a concise requirements summary with:
    1) business goals,
    2) technical constraints,
    3) key risks,
    4) acceptance criteria candidates.
    Keep it compact and pass-forward ready for the next agent.
    """);

ConsoleRenderer.Rule("Etap 2 — solution-architect + deterministic process");
// Przepływ pracy może łączyć pracę agenta opartego na SDK z deterministyczną analizą lokalną.
// Sesja Copilot działa równolegle z analizą StaticAnalysis, ponieważ nie współdzielą
// modyfikowalnego stanu sesji SDK.
var deterministicTask = Task.Run(() => StaticAnalysis.Analyze(projectRoot));
var architectTask = RunAgentStageAsync(
    client,
    model,
    provider,
    projectRoot,
    customAgents,
    "solution-architect",
    $"""
    Here is the requirements summary from the previous SDLC agent:
    {stage1.Summary}

    Build an implementation architecture.

    You MUST delegate at least two focused investigations to subagents via the task tool:
    - one security-focused,
    - one implementation feasibility/performance-focused.

    After subagents return, produce:
    - architecture proposal,
    - delivery phases,
    - top risks and mitigations,
    - a short handoff summary for the test planner.
    """);

await Task.WhenAll(deterministicTask, architectTask);
var deterministicReport = deterministicTask.Result;
var stage2 = architectTask.Result;

var mergedArchitectureSummary =
    $"{stage2.Summary}{Environment.NewLine}{Environment.NewLine}{deterministicReport.ToMarkdown()}";

ConsoleRenderer.Rule("Etap 3 — test-planner");
var stage3 = await RunAgentStageAsync(
    client,
    model,
    provider,
    projectRoot,
    customAgents,
    "test-planner",
    $"""
    Create a test strategy based on this architect handoff:
    {mergedArchitectureSummary}

    Output:
    1) test pyramid (unit/integration/e2e),
    2) critical test scenarios,
    3) deterministic quality gates for CI/CD,
    4) release readiness checklist.
    Keep the result practical and execution-ready.
    """);

ConsoleRenderer.Rule("Podsumowanie pipeline");
var summaryTable = new Table().RoundedBorder().AddColumn("Agent").AddColumn("Summary");
summaryTable.AddRow("requirements-analyst", Truncate(stage1.Summary, 280));
summaryTable.AddRow("solution-architect", Truncate(stage2.Summary, 280));
summaryTable.AddRow("test-planner", Truncate(stage3.Summary, 280));
AnsiConsole.Write(summaryTable);
AnsiConsole.WriteLine();

ConsoleRenderer.Rule("Deterministyczny raport");
AnsiConsole.WriteLine(deterministicReport.ToMarkdown());
AnsiConsole.WriteLine();

var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "agent-workflows-report.md");
await File.WriteAllTextAsync(
    outputPath,
    $"""
    # Agent Workflows Report

    ## Stage 1 — requirements-analyst
    {stage1.Summary}

    ## Stage 2 — solution-architect
    {stage2.Summary}

    {deterministicReport.ToMarkdown()}

    ## Stage 3 — test-planner
    {stage3.Summary}
    """);

ConsoleRenderer.Success($"Raport zapisany: {outputPath}");
ConsoleRenderer.Info("Demo pokazało: on-disk custom agents + subagent delegation + deterministyczna orkiestracja.");
return 0;

static async Task<StageResult> RunAgentStageAsync(
    CopilotClient client,
    string model,
    ProviderConfig? provider,
    string workingDirectory,
    IReadOnlyList<CustomAgentConfig> customAgents,
    string agentName,
    string prompt)
{
    var discoveredAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var subagentActivity = new List<string>();

    // Każdy etap przepływu pracy jest izolowany w osobnej sesji CopilotSession. Agent decyduje,
    // który monit agenta niestandardowego/na dysku powinien zostać użyty przez SDK w tej sesji.
    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        Agent = agentName,
        // WorkingDirectory daje wybranemu agentowi katalog główny projektu na potrzeby
        // wykrywania konfiguracji, odwołań do plików i wszelkich dozwolonych narzędzi.
        WorkingDirectory = workingDirectory,
        // EnableConfigDiscovery umożliwia SDK ładowanie konfiguracji agenta o zasięgu
        // repozytorium z katalogu roboczego.
        EnableConfigDiscovery = true,
        // Tryb tylko-lokalny ogranicza ładowanie agentów niestandardowych do tego przykładowego
        // projektu, zamiast definicji agentów użytkownika/globalnych.
        CustomAgentsLocalOnly = true,
        // CustomAgents dostarcza sparsowane definicje z .github/agents bezpośrednio
        // do sesji SDK.
        CustomAgents = customAgents.ToList(),
        // Zdarzenia strumieniowe subagentów pozwalają hostowi obserwować pracę delegowaną
        // przez agenta, co jest przydatne do nauczania orkiestracji.
        IncludeSubAgentStreamingEvents = true,
        OnPermissionRequest = PermissionHandler.ApproveAll,
    });

    using var eventLog = session.On<SessionEvent>(evt =>
    {
        // Interfejs użytkownika przepływu pracy nasłuchuje zdarzeń SDK specyficznych dla
        // agentów niestandardowych i subagentów. Tekst asystenta obsługuje SendAndWaitAsync.
        switch (evt)
        {
            case SessionCustomAgentsUpdatedEvent agentsUpdated:
                // Emitowane, gdy SDK załadował/zaktualizował listę agentów niestandardowych
                // dostępnych w tej sesji.
                foreach (var agent in agentsUpdated.Data.Agents ?? [])
                {
                    if (discoveredAgents.Add(agent.Name ?? "(unknown)"))
                        ConsoleRenderer.Info($"Załadowano agenta: {agent.Name} (source: {agent.Source})");
                }
                break;
            case SubagentStartedEvent started:
                // Emitowane, gdy agent deleguje konkretne zadanie subagentowi.
                subagentActivity.Add($"started:{started.Data.AgentName ?? started.Data.AgentDisplayName ?? "unknown"}");
                ConsoleRenderer.Warn($"Subagent started: {started.Data.AgentName ?? started.Data.AgentDisplayName ?? "unknown"}");
                break;
            case SubagentCompletedEvent completed:
                // Emitowane, gdy delegowany subagent zwraca kontrolę/wyniki
                // do agenta nadrzędnego.
                subagentActivity.Add($"completed:{completed.Data.AgentName ?? completed.Data.AgentDisplayName ?? "unknown"}");
                ConsoleRenderer.Success($"Subagent completed: {completed.Data.AgentName ?? completed.Data.AgentDisplayName ?? "unknown"}");
                break;
        }
    });

    var response = await ConsoleRenderer.SpinnerAsync(
        $"Uruchamiam {agentName}...",
        // Monit to pojedyncza tura SDK dla wybranego agenta. Zakończenie opiera się
        // na SessionIdleEvent wewnątrz helpera.
        () => SessionHelper.SendAndWaitAsync(session, prompt));

    if (subagentActivity.Count > 0)
        ConsoleRenderer.Info($"{agentName}: subagent events={subagentActivity.Count}");

    return new StageResult(agentName, response.Trim());
}

static string Truncate(string text, int max) =>
    text.Length > max ? text[..max] + "…" : text;

static List<CustomAgentConfig> LoadCustomAgents(string agentsDirectory)
{
    if (!Directory.Exists(agentsDirectory))
        throw new DirectoryNotFoundException($"Nie znaleziono katalogu custom agents: {agentsDirectory}");

    var agents = new List<CustomAgentConfig>();

    foreach (var filePath in Directory.EnumerateFiles(agentsDirectory, "*.md").Order())
    {
        var markdown = File.ReadAllText(filePath);
        var (frontmatter, prompt) = ParseAgentMarkdown(markdown, filePath);
        var name = GetRequiredFrontmatterValue(frontmatter, "name", filePath);

        // CustomAgentConfig to DTO SDK dla definicji agenta: metadane,
        // dozwolone narzędzia i treść promptu sparsowana z pliku markdown.
        agents.Add(new CustomAgentConfig
        {
            Name = name,
            DisplayName = GetFrontmatterValue(frontmatter, "display-name") ?? name,
            Description = GetFrontmatterValue(frontmatter, "description"),
            Tools = ParseTools(GetFrontmatterValue(frontmatter, "tools")),
            Prompt = prompt.Trim(),
        });
    }

    return agents;
}

static (Dictionary<string, string> Frontmatter, string Prompt) ParseAgentMarkdown(string markdown, string filePath)
{
    using var reader = new StringReader(markdown);

    if (reader.ReadLine()?.Trim() != "---")
        throw new InvalidDataException($"Agent nie ma frontmatter: {filePath}");

    var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? line;

    while ((line = reader.ReadLine()) is not null)
    {
        if (line.Trim() == "---")
            break;

        var separatorIndex = line.IndexOf(':');
        if (separatorIndex <= 0)
            continue;

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();
        frontmatter[key] = value;
    }

    if (line is null)
        throw new InvalidDataException($"Agent ma niezamknięty frontmatter: {filePath}");

    return (frontmatter, reader.ReadToEnd());
}

static string? GetFrontmatterValue(Dictionary<string, string> frontmatter, string key) =>
    frontmatter.TryGetValue(key, out var value) ? TrimYamlString(value) : null;

static string GetRequiredFrontmatterValue(Dictionary<string, string> frontmatter, string key, string filePath) =>
    GetFrontmatterValue(frontmatter, key)
    ?? throw new InvalidDataException($"Agent nie ma wymaganego pola '{key}': {filePath}");

static List<string>? ParseTools(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    value = value.Trim();

    if (value.StartsWith('[') && value.EndsWith(']'))
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(value);
        }
        catch (System.Text.Json.JsonException)
        {
            return value[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(TrimYamlString)
                .ToList();
        }
    }

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(TrimYamlString)
        .ToList();
}

static string TrimYamlString(string value) =>
    value.Trim().Trim('"', '\'');


internal sealed record StageResult(string Agent, string Summary);
