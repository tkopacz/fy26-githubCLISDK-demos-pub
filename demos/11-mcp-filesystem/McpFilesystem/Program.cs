// Demo 11 — McpFilesystem: Panel kontrolny stanu projektu
//
// Pokazuje: SessionConfig.McpServers z @modelcontextprotocol/server-filesystem (stdio/npx).
// SDK analizuje strukturę projektu przez MCP — widoczne jako ExternalToolRequestedEvent.
// Wszystkie operacje plikowe przez MCP, nie własne tools.
//
// Wymagania: Node.js + npx (do uruchomienia MCP server)
// Uruchomienie: dotnet run --project demos/11-mcp-filesystem/McpFilesystem [katalog]

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("MCP Filesystem", "Demo 11 — GitHub Copilot SDK: Project Health Dashboard");
ConsoleRenderer.Info("Analizuje projekt przez MCP @modelcontextprotocol/server-filesystem.\n");

// Sprawdź dostępność npx
var npxCommand = ResolveNpxCommand();
if (npxCommand is null || !await IsNpxAvailableAsync(npxCommand))
{
    ConsoleRenderer.Error("npx nie jest dostępne. Zainstaluj Node.js (https://nodejs.org) i spróbuj ponownie.");
    ConsoleRenderer.Info("Demo 11 wymaga Node.js do uruchomienia MCP server-filesystem.");
    return 1;
}

var projectPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "SampleProject");

if (!Directory.Exists(projectPath))
{
    ConsoleRenderer.Error($"Katalog projektu nie istnieje: {projectPath}");
    return 1;
}

ConsoleRenderer.Info($"Projekt: {projectPath}");
AnsiConsole.MarkupLine("[grey]MCP server: @modelcontextprotocol/server-filesystem (stdio via npx)[/]\n");

// CopilotClient jest właścicielem środowiska wykonawczego SDK. Serwery MCP nie są globalnym stanem
// klienta; są podłączane do konkretnej sesji CopilotSession, która musi zezwolić na użycie
// tych zewnętrznych narzędzi.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId();
var provider = CopilotClientFactory.GetByokProvider();

// Licznik operacji MCP widocznych poprzez ExternalToolRequestedEvent. Narzędzia
// MCP są zewnętrzne względem tego procesu .NET, ale SDK nadal raportuje, gdy
// model prosi o wywołanie któregoś z nich.
var mcpOperations = new List<McpOperation>();
// Ograniczamy serwer MCP do narzędzi systemu plików tylko do odczytu. Ta lista jest przekazywana
// do sesji SDK, dzięki czemu model widzi wyłącznie dozwolone możliwości MCP.
string[] readOnlyFilesystemTools =
[
    "read_file",
    "read_multiple_files",
    "list_directory",
    "directory_tree",
    "search_files",
    "get_file_info",
    "list_allowed_directories",
];

await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = model,
    Provider = provider,
    // Wywołania narzędzi MCP są nadal kontrolowane przez SDK. Ta procedura obsługi zezwala
    // na operacje systemu plików tylko do odczytu i odrzuca znane operacje mutujące.
    OnPermissionRequest = ReadOnlyMcpPermissionAsync,
    // PreToolUse to drugi strażnik, który widzi konkretną nazwę narzędzia tuż
    // przed wykonaniem. Blokuje mutujące narzędzia systemu plików, nawet jeśli serwer
    // niespodziewanie je udostępni.
    Hooks = new SessionHooks
    {
        OnPreToolUse = (input, _) =>
        {
            if (!IsMutatingFilesystemTool(input.ToolName))
                return Task.FromResult<PreToolUseHookOutput?>(null);

            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "deny",
                AdditionalContext = "BLOCKED: Demo 11 exposes only read-only filesystem MCP tools.",
            });
        },
    },
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are a project health analyzer. Use the filesystem MCP server tools to analyze
            the project structure and generate a health dashboard.

            Focus on:
            1. File structure: count files per type (.cs, .json, .md, etc.), identify missing README files
            2. Code quality: find TODO/FIXME/HACK comments in source files
            3. Test coverage: identify test files, compare to source files count
            4. Documentation: check for README.md presence in key directories
            5. Configuration: verify appsettings.json, .gitignore presence

            Use the filesystem tools (list_directory, read_file, search_files) to explore the project.
            Be thorough — explore subdirectories to get accurate counts.

            Output a structured health report with:
            - Overall health score (0-100)
            - Issues found (grouped by category)
            - Recommendations (prioritized)
            """,
    },
    // McpServers podłącza zewnętrzne serwery Model Context Protocol do tej
    // sesji. SDK uruchamia skonfigurowany serwer, wykrywa jego narzędzia i
    // pozwala modelowi wywoływać je w tej samej pętli agenta, co natywne
    // narzędzia AIFunction.
    McpServers = new Dictionary<string, McpServerConfig>
    {
        ["filesystem"] = new McpStdioServerConfig
        {
            // Stdio MCP oznacza, że SDK uruchamia proces potomny i komunikuje się z nim
            // przez standardowe wejście/wyjście. Tutaj npx uruchamia oficjalny serwer MCP
            // systemu plików ograniczony do projectPath.
            Command = npxCommand,
            Args = ["-y", "@modelcontextprotocol/server-filesystem", projectPath],
            // Filtrowanie narzędzi utrzymuje udostępnianą powierzchnię MCP sesji w trybie
            // tylko do odczytu, mimo że bazowy serwer obsługuje więcej narzędzi.
            Tools = readOnlyFilesystemTools,
        },
    },
});

// Nasłuchujemy operacji MCP. ExternalToolRequestedEvent/ExternalToolCompletedEvent to
// haki widoczności SDK dla narzędzi hostowanych poza procesem .NET.
session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case ExternalToolRequestedEvent req:
            // Requested oznacza, że model wybrał narzędzie MCP i SDK
            // zamierza przekazać to wywołanie do zewnętrznego serwera MCP.
            var toolName = req.Data.ToolName ?? "mcp-call";
            var reqId = req.Data.RequestId ?? "";
            mcpOperations.Add(new McpOperation(toolName, reqId));
            AnsiConsole.MarkupLine($"  [cyan]→ MCP tool:[/] {toolName} [grey](req: {reqId[..Math.Min(8, reqId.Length)]}...)[/]");
            break;

        case ExternalToolCompletedEvent done:
            // Completed oznacza, że serwer MCP zwrócił wynik i SDK może przekazać
            // go z powrotem do kontekstu rozmowy modelu.
            var doneId = done.Data.RequestId ?? "";
            AnsiConsole.MarkupLine($"  [green]✓ MCP done:[/] [grey]{doneId[..Math.Min(8, doneId.Length)]}...[/]");
            break;
    }
});

// Uruchom analizę
ConsoleRenderer.Rule("Analiza projektu przez MCP filesystem");
ConsoleRenderer.Info("Obserwuj operacje MCP (→ MCP tool) w czasie rzeczywistym...\n");

var result = await ConsoleRenderer.SpinnerAsync(
    "Model eksploruje projekt przez MCP...",
    // Prompt mówi modelowi, co ma osiągnąć; rzeczywisty dostęp do systemu plików
    // odbywa się wyłącznie poprzez narzędzia MCP udostępnione w SessionConfig.McpServers.
    () => SessionHelper.SendAndWaitAsync(session,
        $"""
        Analyze the project at the root directory accessible through the filesystem MCP server.

        Steps:
        1. List the root directory contents
        2. Recursively explore subdirectories (src/, tests/, docs/ if they exist)
        3. Read source files to find TODO/FIXME/HACK comments
        4. Check for README.md files in key directories
        5. Count files by type
        6. Generate the health dashboard report

        Project path being analyzed: {projectPath}
        """));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Raport zdrowia projektu");
AnsiConsole.WriteLine(result);

// Podsumowanie operacji MCP
AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Statystyki MCP");
AnsiConsole.MarkupLine($"[bold]Łączna liczba operacji MCP:[/] [cyan]{mcpOperations.Count}[/]");

if (mcpOperations.Count > 0)
{
    var grouped = mcpOperations
        .GroupBy(o => o.Tool)
        .OrderByDescending(g => g.Count())
        .Take(5);

    var table = new Table().RoundedBorder()
        .AddColumn("Narzędzie MCP")
        .AddColumn("Liczba wywołań");

    foreach (var g in grouped)
        table.AddRow(g.Key, g.Count().ToString());

    AnsiConsole.Write(table);
}

ConsoleRenderer.Info("\nWszystkie operacje plikowe przez MCP — żaden kod C# nie dotknął filesystemu bezpośrednio.");
ConsoleRenderer.Info("Demo pokazało: McpStdioServerConfig, ExternalToolRequestedEvent, sessionConfig.McpServers dict.");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

static async Task<bool> IsNpxAvailableAsync(string npxCommand)
{
    try
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = npxCommand,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (proc is null) return false;
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

static string? ResolveNpxCommand()
{
    var candidates = OperatingSystem.IsWindows()
        ? new[] { "npx.cmd", "npx.exe" }
        : ["npx"];

    return candidates
        .Select(FindOnPath)
        .FirstOrDefault(path => path is not null);
}

static string? FindOnPath(string executableName)
{
    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
        return null;

    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory.Trim(), executableName);
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

#pragma warning disable GHCP001
static Task<PermissionDecision> ReadOnlyMcpPermissionAsync(
    PermissionRequest request,
    PermissionInvocation _)
{
    // PermissionRequest.Kind identyfikuje operację narzędzia, o którą prosi SDK.
    // W przypadku wywołań MCP może ona zawierać kwalifikację serwera/nazwy, dlatego dopasowanie
    // stosuje poniżej normalizację uwzględniającą przyrostki.
    var kind = request.Kind?.ToString() ?? string.Empty;
    return Task.FromResult(IsMutatingFilesystemTool(kind)
        ? PermissionDecision.UserNotAvailable()
        : PermissionDecision.ApproveOnce());
}
#pragma warning restore GHCP001

static bool IsMutatingFilesystemTool(string toolName) =>
    ToolMatches(toolName,
        "write_file",
        "edit_file",
        "move_file",
        "delete_file",
        "remove_file",
        "create_directory",
        "create_file");

static bool ToolMatches(string toolName, params string[] names)
{
    var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
    return names.Any(name =>
        normalized == name ||
        normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($":{name}", StringComparison.Ordinal));
}

record McpOperation(string Tool, string RequestId);
