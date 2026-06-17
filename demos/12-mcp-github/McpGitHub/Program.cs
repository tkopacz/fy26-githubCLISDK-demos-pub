// Demo 12 — McpGitHub: Brama jakości PR
//
// Pokazuje: McpHttpServerConfig z GitHub MCP server (https://api.githubcopilot.com/mcp/).
// SDK przez GitHub MCP pobiera diff PR, zmienione pliki, issues, komentarze.
// Generuje: non-technical summary, pytania do autora, rekomendację approve/request-changes.
//
// Wymagania: GITHUB_TOKEN env var z prawem do odczytu PR
// Uruchomienie: dotnet run --project demos/12-mcp-github/McpGitHub -- owner/repo PR-number
// Przykład:   dotnet run --project demos/12-mcp-github/McpGitHub -- dotnet/runtime 100000

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("MCP GitHub", "Demo 12 — GitHub Copilot SDK: PR Quality Gate");
ConsoleRenderer.Info("Analizuje Pull Request przez GitHub MCP server.\n");

// Sprawdź GITHUB_TOKEN
var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(githubToken))
{
    ConsoleRenderer.Error("Brak zmiennej środowiskowej GITHUB_TOKEN.");
    ConsoleRenderer.Info("Ustaw: $env:GITHUB_TOKEN = 'ghp_...' i uruchom ponownie.");
    return 1;
}

// Parsuj argumenty
string owner, repo;
int prNumber;

if (args.Length >= 2 && args[0].Contains('/') && int.TryParse(args[1], out prNumber))
{
    var parts = args[0].Split('/');
    owner = parts[0];
    repo = parts[1];
}
else if (args.Length >= 1 && args[0].Contains('/'))
{
    // Format: owner/repo#PR
    var repoArg = args[0];
    var hashIdx = repoArg.IndexOf('#');
    if (hashIdx > 0 && int.TryParse(repoArg[(hashIdx + 1)..], out prNumber))
    {
        var parts = repoArg[..hashIdx].Split('/');
        owner = parts[0];
        repo = parts[1];
    }
    else
    {
        ConsoleRenderer.Error("Niepoprawny format. Użycie: dotnet run -- owner/repo PR-number");
        ConsoleRenderer.Info("Przykład: dotnet run -- microsoft/vscode 12345");
        return 1;
    }
}
else
{
    // Tryb demonstracyjny — przykładowy PR z dobrze znanego repozytorium
    ConsoleRenderer.Warn("Brak argumentów. Używam przykładowego publicznego PR (microsoft/vscode #220000).");
    ConsoleRenderer.Info("Użycie: dotnet run -- owner/repo PR-number\n");
    owner = "microsoft";
    repo = "vscode";
    prNumber = 220000;
}

ConsoleRenderer.Info($"PR: {owner}/{repo}#{prNumber}");
AnsiConsole.MarkupLine("[grey]MCP server: https://api.githubcopilot.com/mcp/ (HTTP)[/]\n");
ConsoleRenderer.Info("Użyj fine-grained GITHUB_TOKEN z uprawnieniami tylko do odczytu PR, contents i issues.\n");

// Klient SDK jest właścicielem połączenia środowiska wykonawczego. Serwer GitHub MCP jest podłączony
// do poniższej sesji, więc tylko ta rozmowa przeglądu PR otrzyma
// zewnętrzne narzędzia GitHub.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId();
var provider = CopilotClientFactory.GetByokProvider();

// Licznik operacji GitHub MCP. Zewnętrzne narzędzia MCP nie są funkcjami C# w tym
// procesie, ale SDK nadal emituje zdarzenia, gdy model je wywołuje.
var mcpCalls = 0;
// Utrzymujemy udostępnianą powierzchnię GitHub MCP w trybie tylko do odczytu. To demo przegląda dane PR;
// nie może publikować komentarzy, zatwierdzać, scalać, etykietować ani zmieniać stanu repozytorium.
string[] readOnlyGitHubTools =
[
    "get_pull_request",
    "get_pull_request_files",
    "get_pull_request_status",
    "get_pull_request_reviews",
    "get_pull_request_comments",
    "get_pull_request_diff",
    "get_issue",
    "get_issue_comments",
    "list_issues",
    "list_pull_requests",
    "search_issues",
    "search_pull_requests",
    "get_file_contents",
];

await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = model,
    Provider = provider,
    // Wywołania MCP przechodzą przez ten sam model haka uprawnień SDK, co narzędzia natywne.
    // Ta procedura obsługi zatwierdza operacje tylko do odczytu i odrzuca znane mutujące
    // operacje GitHub.
    OnPermissionRequest = ReadOnlyGitHubMcpPermissionAsync,
    // PreToolUse to obrona w głąb (defense-in-depth), która sprawdza konkretną nazwę
    // zewnętrznego narzędzia tuż przed wysłaniem przez SDK wywołania do serwera MCP.
    Hooks = new SessionHooks
    {
        OnPreToolUse = (input, _) =>
        {
            if (!IsMutatingGitHubTool(input.ToolName))
                return Task.FromResult<PreToolUseHookOutput?>(null);

            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "deny",
                AdditionalContext = "BLOCKED: Demo 12 reviews PRs with read-only GitHub MCP tools only.",
            });
        },
    },
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are an expert code reviewer and technical writer performing a PR quality gate check.
            Use the GitHub MCP tools to gather information about the PR, then generate:

            1. NON-TECHNICAL SUMMARY (3-5 sentences): Explain the PR's purpose and impact for
               a non-technical stakeholder (product manager, CTO). No code jargon.

            2. QUESTIONS FOR AUTHOR (3-5 bullet points): Specific questions about design decisions,
               edge cases, or missing tests that the author should address before merging.

            3. REVIEW RECOMMENDATION: Choose one of:
               - ✅ APPROVE — if the PR is clearly correct and complete
               - 🔄 REQUEST CHANGES — if there are specific issues to address
               - ❓ NEEDS DISCUSSION — if there are architectural questions

            4. DRAFT REVIEW COMMENT: A professional, constructive comment ready to post on GitHub.
               Be specific, cite file names and line numbers where relevant.

            Always use MCP tools to read the actual PR data before writing your review.
            Never post, submit, approve, merge, label, close, or otherwise mutate GitHub state.
            """,
    },
    // McpHttpServerConfig łączy się ze zdalnym punktem końcowym MCP zamiast uruchamiać
    // lokalny proces potomny stdio. SDK odkrywa narzędzia z tego punktu końcowego
    // i pozwala modelowi wywoływać poniższy, filtrowany zestaw tylko do odczytu.
    McpServers = new Dictionary<string, McpServerConfig>
    {
        ["github"] = new McpHttpServerConfig
        {
            Url = "https://api.githubcopilot.com/mcp/",
            // Nagłówki są wysyłane przez SDK podczas komunikacji z serwerem MCP. Model
            // nigdy nie otrzymuje wartości tokenu; widzi jedynie schematy dostępnych
            // narzędzi oraz dane zwracane przez zatwierdzone wywołania narzędzi.
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {githubToken}",
            },
            // Filtrowanie narzędzi ogranicza to, o co model może poprosić serwer GitHub MCP
            // w trakcie tej sesji.
            Tools = readOnlyGitHubTools,
        },
    },
});

// Śledzimy wywołania narzędzi zewnętrznych. Przy HTTP MCP te zdarzenia są najprostszym sposobem,
// by pokazać, że szczegóły PR, diffy, komentarze i status przeszły przez kanał SDK/MCP,
// a nie przez bezpośrednie wywołania HTTP w kodzie aplikacji.
session.On<SessionEvent>(evt =>
{
    if (evt is ExternalToolRequestedEvent req)
    {
        // Requested oznacza, że model wybrał narzędzie GitHub MCP, a SDK
        // przekazuje (proxy) to wywołanie do skonfigurowanego punktu końcowego MCP.
        mcpCalls++;
        var tool = req.Data.ToolName ?? "github-mcp";
        AnsiConsole.MarkupLine($"  [cyan]→ MCP:[/] {tool}");
    }
});

// Uruchom analizę PR
ConsoleRenderer.Rule("Analiza PR przez GitHub MCP");
ConsoleRenderer.Info("Model pobiera dane PR przez MCP (obserwuj wywołania →)...\n");

var review = await ConsoleRenderer.SpinnerAsync(
    $"Analizuję PR #{prNumber} w {owner}/{repo}...",
    // Prompt definiuje przepływ pracy recenzji; gromadzenie danych następuje, gdy
    // model wywołuje narzędzia GitHub MCP tylko do odczytu udostępnione w tej sesji.
    () => SessionHelper.SendAndWaitAsync(session,
        $"""
        Please perform a full PR quality gate review for:
        - Repository: {owner}/{repo}
        - PR Number: {prNumber}

        Steps:
        1. Use GitHub MCP to get the PR details (title, description, status, author)
        2. Get the list of changed files and their diffs
        3. Check for linked issues or related context
        4. Review any existing review comments
        5. Generate the complete quality gate report as described in your system message

        Focus on: code quality, completeness, potential bugs, missing tests, security concerns.
        """));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule($"Quality Gate — {owner}/{repo}#{prNumber}");
AnsiConsole.WriteLine(review);

// Statystyki
AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Statystyki MCP");
AnsiConsole.MarkupLine($"[bold]Operacje GitHub MCP:[/] [cyan]{mcpCalls}[/]");
ConsoleRenderer.Info($"Wszystkie dane PR pobrane przez MCP — brak bezpośrednich HTTP calls z C# kodu.");
ConsoleRenderer.Info("Demo pokazało: McpHttpServerConfig, Authorization headers, GitHub MCP API.");
return 0;

#pragma warning disable GHCP001
static Task<PermissionDecision> ReadOnlyGitHubMcpPermissionAsync(
    PermissionRequest request,
    PermissionInvocation _)
{
    // PermissionRequest.Kind może mieć kwalifikację serwerową dla narzędzi MCP, dlatego dopasowanie
    // uwzględnia przyrostki. Narzędzia mutujące są odrzucane, nawet jeśli w jakiś sposób pojawią się
    // w udostępnianych możliwościach serwera.
    var kind = request.Kind?.ToString() ?? string.Empty;
    return Task.FromResult(IsMutatingGitHubTool(kind)
        ? PermissionDecision.UserNotAvailable()
        : PermissionDecision.ApproveOnce());
}
#pragma warning restore GHCP001

static bool IsMutatingGitHubTool(string toolName) =>
    ToolMatches(toolName,
        "add_issue_comment",
        "add_pull_request_review_comment",
        "create_branch",
        "create_issue",
        "create_issue_comment",
        "create_or_update_file",
        "create_pull_request",
        "create_pull_request_review",
        "delete_file",
        "delete_issue_comment",
        "dismiss_pull_request_review",
        "merge_pull_request",
        "push_files",
        "request_copilot_review",
        "submit_pull_request_review",
        "update_issue",
        "update_issue_comment",
        "update_pull_request",
        "update_pull_request_branch");

static bool ToolMatches(string toolName, params string[] names)
{
    var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
    return names.Any(name =>
        normalized == name ||
        normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($":{name}", StringComparison.Ordinal));
}
