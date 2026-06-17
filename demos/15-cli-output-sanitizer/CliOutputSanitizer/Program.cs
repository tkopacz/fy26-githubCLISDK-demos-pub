// Demo 15 — CliOutputSanitizer: narzędzie CLI oszczędzające tokeny
//
// Pokazuje: jak wbudować "RTK" (tool) w pipeline GitHub Copilot SDK:
// 1) narzędzie uruchamia polecenie CLI,
// 2) czyszczenie outputu odbywa się w C# przed zwróceniem do modelu,
// 3) model dostaje krótszy tekst, więc oszczędza tokeny.
// To jest kluczowy przykład dla prezentacji: tool działa w procesie, a nie jako zewnętrzny wrapper.
//
// Uruchomienie:
//   dotnet run --project demos/15-cli-output-sanitizer/CliOutputSanitizer

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using CopilotSDK.Demos.Demos.CliOutputSanitizer;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("CLI Sanitizer", "Demo 15 — GitHub Copilot SDK: Token-Saving CLI Tool");
ConsoleRenderer.Info("Pokażę, jak narzędzie może wyczyścić output poleceń CLI zanim trafi do modelu. Mniej szumu = mniej tokenów.\n");
ConsoleRenderer.Info("Najlepsze kandydatki do sanitizacji: `git --no-pager log --oneline --graph -10`, `dotnet list package --include-transitive`, `dir /B` i `Get-ChildItem -Force`.\n");
RenderSanitizationExample();

var commandTimeout = TimeSpan.FromSeconds(15);
const int maxRawStreamChars = 24_000;
const int maxSanitizedOutputChars = 12_000;
var allowedCliCommands = BuildAllowedCliCommands();

// Klient SDK obsługuje środowisko wykonawcze; polecenie Runner jest udostępniane w trakcie sesji
// jako narzędzie, dzięki czemu to demo może ściśle określać, co model może wywoływać.
await using var client = CopilotClientFactory.Create();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    // Hak uprawnień zezwala tylko na narzędzie sanitizujące. Nawet jeśli model poprosi
    // o inną operację SDK, host odmawia, zwracając UserNotAvailable.
    OnPermissionRequest = AllowRunCliCommandPermissionAsync,
    // Komunikat systemowy jest ważny w tym demie SDK: instruuje model, aby
    // używał zarejestrowanego narzędzia do diagnostyki CLI zamiast zmyślać wyjście
    // poleceń lub żądać dowolnego dostępu do powłoki.
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are a CLI diagnostics assistant.
            Use run_cli_command for every shell command.
            Return concise findings and mention the token savings the sanitizer achieved.
            """,
    },
    Tools =
    [
        // To narzędzie jest głównym punktem integracji w tym demie SDK. Model widzi
        // schemat z jednym parametrem CommandId; host mapuje ten identyfikator na
        // polecenie z listy dozwolonych, uruchamia je, czyści dane wyjściowe i zwraca
        // kompaktowy JSON jako wynik narzędzia.
        AIFunctionFactory.Create(
            ([Description("Allowed command id: dotnet_info, dotnet_packages, git_log, list_files, or list_all_files")] string commandId) =>
                RunCliCommandAsync(commandId),
            new AIFunctionFactoryOptions
            {
                Name = "run_cli_command",
                Description = "Executes one allowlisted diagnostic CLI command by id and returns sanitized output.",
            }),
    ],
});

using var eventLog = session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case ToolExecutionStartEvent tool:
            // Start oznacza, że model wybrał run_cli_command i wygenerował
            // argumenty pasujące do schematu AIFunction.
            AnsiConsole.MarkupLine($"[yellow]⚙ TOOL[/] {tool.Data.ToolName.Replace("[", "[[").Replace("]", "]]")}");
            break;

        case ToolExecutionCompleteEvent tool:
            // Complete przenosi oczyszczony wynik JSON z powrotem przez SDK, więc
            // model widzi wyczyszczone wyjście, a nie surowy szum terminala.
            // Podgląd jest przeznaczony wyłącznie dla człowieka oglądającego demo.
            if (TryReadCliCommandResult(tool.Data.Result, out var result))
            {
                var saved = result.EstimatedTokensSaved;
                var sanitized = result.SanitizedOutput;
                var preview = sanitized.Length > 140 ? sanitized[..140] + "…" : sanitized;

                AnsiConsole.MarkupLine($"   [green]✓[/] sanitized={sanitized.Length} chars, saved≈{saved} tokens");
                if (!string.IsNullOrWhiteSpace(preview))
                    AnsiConsole.MarkupLine($"   [dim]{preview.Replace("[", "[[").Replace("]", "]]")}[/] ");
            }
            else
            {
                AnsiConsole.MarkupLine("   [dim](tool result could not be parsed for preview)[/]");
            }

            break;
    }
});

var prompt = """
    Use run_cli_command to inspect the environment in this repo.
    Please run these allowed command IDs one by one, especially the ones that are noisy and benefit from sanitization:
    1. dotnet_info
    2. dotnet_packages
    3. git_log
    4. list_files
    5. list_all_files
    Then summarize the findings and explicitly mention the token savings from the sanitizer.
    """;

var answer = await ConsoleRenderer.SpinnerAsync(
    "Agent analizuje środowisko i używa narzędzia CLI...",
    // Monit opisuje cały przepływ pracy; model wybiera poszczególne wywołania narzędzi,
    // a SessionHelper czeka, aż SDK wyemituje SessionIdleEvent.
    () => SessionHelper.SendAndWaitAsync(session, prompt));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Wynik agenta");
Console.WriteLine(answer);

void RenderSanitizationExample()
{
    const string raw = "  dotnet   --info      \n\n  ═══════════════\n  git   log   --oneline   -10  \n";
    var sanitized = OutputSanitizer.Sanitize(raw);

    ConsoleRenderer.Rule("Preview: przed i po sanitizacji");
    AnsiConsole.MarkupLine("[yellow]To jest dokładnie ten szum, który chcemy usunąć: ANSI, dekoracje i nadmiarowe spacje.[/]");

    AnsiConsole.Write(new Panel(raw.Replace("[", "[[").Replace("]", "]]")).Header("RAW", Justify.Left).Border(BoxBorder.Rounded).BorderStyle("red"));
    AnsiConsole.Write(new Panel(sanitized.Replace("[", "[[").Replace("]", "]]")).Header("SANITIZED", Justify.Left).Border(BoxBorder.Rounded).BorderStyle("green"));
}

bool TryReadCliCommandResult(object? resultValue, out CliCommandResult result)
{
    // Aktualne SDK opakowuje wyniki AIFunction w ToolResultObject; model dostaje
    // JSON przez TextResultForLlm, a panel demo potrzebuje z niego statystyk sanitizacji.
    switch (resultValue)
    {
        case CliCommandResult directResult:
            result = directResult;
            return true;
        case ToolResultObject toolResult:
            return TryReadCliCommandResult(toolResult.TextResultForLlm, out result);
        case ToolExecutionCompleteResult completeResult:
            return TryReadCliCommandResult(completeResult.Content, out result) ||
                TryReadCliCommandResult(completeResult.DetailedContent, out result);
        case JsonElement element:
            return TryDeserializeCliCommandResult(element.GetRawText(), out result);
        case string json:
            return TryDeserializeCliCommandResult(json, out result);
        default:
            result = default!;
            return false;
    }
}

bool TryDeserializeCliCommandResult(string json, out CliCommandResult result)
{
    try
    {
        var parsed = JsonSerializer.Deserialize<CliCommandResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (parsed is not null)
        {
            result = parsed;
            return true;
        }
    }
    catch (JsonException)
    {
    }

    result = default!;
    return false;
}

async Task<CliCommandResult> RunCliCommandAsync(string commandId)
{
    // Argumenty narzędzia są generowane przez model, więc host tłumaczy identyfikator
    // polecenia przez ścisłą listę dozwolonych zamiast wykonywać dowolny tekst powłoki.
    var requestedCommandId = commandId.Trim();
    if (!allowedCliCommands.TryGetValue(requestedCommandId, out var command))
    {
        const string message = "Command rejected: use one of dotnet_info, dotnet_packages, git_log, list_files, or list_all_files.";
        return new CliCommandResult(
            Command: requestedCommandId,
            ExitCode: null,
            TimedOut: false,
            Rejected: true,
            Truncated: false,
            RawCharacterCount: 0,
            SanitizedCharacterCount: message.Length,
            EstimatedTokensSaved: 0,
            SanitizedOutput: message);
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = command.FileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Directory.GetCurrentDirectory(),
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    foreach (var argument in command.Arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Nie udało się uruchomić procesu CLI.");
    var rawOutputTask = ReadBoundedOutputAsync(process.StandardOutput, maxRawStreamChars);
    var errorOutputTask = ReadBoundedOutputAsync(process.StandardError, maxRawStreamChars);

    var timedOut = false;
    using var timeoutCts = new CancellationTokenSource(commandTimeout);
    try
    {
        await process.WaitForExitAsync(timeoutCts.Token);
    }
    catch (OperationCanceledException)
    {
        timedOut = true;
        KillProcess(process);
        await process.WaitForExitAsync();
    }

    var rawOutput = await rawOutputTask;
    var errorOutput = await errorOutputTask;

    var combinedOutput = string.Join("\n", new[] { rawOutput.Output, errorOutput.Output }
        .Where(text => !string.IsNullOrWhiteSpace(text)));
    if (timedOut)
        combinedOutput = $"{combinedOutput}\n\n[process timed out after {commandTimeout.TotalSeconds:n0}s and was stopped]";

    var sanitizedOutput = LimitText(OutputSanitizer.Sanitize(combinedOutput), maxSanitizedOutputChars, out var sanitizedTruncated);
    var rawCharCount = rawOutput.TotalChars + errorOutput.TotalChars;
    var sanitizedCharCount = sanitizedOutput.Length;
    var estimatedTokensSaved = Math.Max(0, (rawCharCount - sanitizedCharCount) / 4);
    var truncated = rawOutput.Truncated || errorOutput.Truncated || sanitizedTruncated;

    // Pokazuj na żywo liczbę zaoszczędzonych tokenów. Model również otrzymuje te wartości
    // jako część wyniku narzędzia, aby mógł zgłosić efekt działania sanitizera.
    ConsoleRenderer.Warn($"CLI raw={rawCharCount} chars, sanitized={sanitizedCharCount} chars, saved≈{estimatedTokensSaved} tokens");

    return new CliCommandResult(
        Command: command.DisplayCommand,
        ExitCode: timedOut ? null : process.ExitCode,
        TimedOut: timedOut,
        Rejected: false,
        Truncated: truncated,
        RawCharacterCount: rawCharCount,
        SanitizedCharacterCount: sanitizedCharCount,
        EstimatedTokensSaved: estimatedTokensSaved,
        SanitizedOutput: sanitizedOutput);
}

#pragma warning disable GHCP001
Task<PermissionDecision> AllowRunCliCommandPermissionAsync(
    PermissionRequest request,
    PermissionInvocation _)
{
    // Dla AIFunction SDK raportuje ogólny Kind=custom_tool, a konkretną nazwę
    // narzędzia przekazuje w PermissionRequestCustomTool.ToolName.
    var toolName = request switch
    {
        PermissionRequestCustomTool customTool => customTool.ToolName,
        PermissionRequestHook hook => hook.ToolName,
        PermissionRequestMcp mcp => mcp.ToolName,
        _ => request.Kind?.ToString() ?? string.Empty,
    };

    return Task.FromResult(ToolMatches(toolName, "run_cli_command")
        ? PermissionDecision.ApproveOnce()
        : PermissionDecision.UserNotAvailable());
}
#pragma warning restore GHCP001

async Task<BoundedOutput> ReadBoundedOutputAsync(TextReader reader, int maxChars)
{
    var buffer = new char[2048];
    var output = new StringBuilder(Math.Min(maxChars, buffer.Length));
    var totalChars = 0;
    var truncated = false;

    while (true)
    {
        var read = await reader.ReadAsync(buffer, 0, buffer.Length);
        if (read == 0)
            break;

        totalChars += read;
        var remaining = maxChars - output.Length;
        if (remaining > 0)
            output.Append(buffer, 0, Math.Min(read, remaining));

        if (read > remaining)
            truncated = true;
    }

    return new BoundedOutput(output.ToString(), totalChars, truncated);
}

string LimitText(string value, int maxChars, out bool truncated)
{
    if (value.Length <= maxChars)
    {
        truncated = false;
        return value;
    }

    truncated = true;
    return value[..maxChars] + "\n[output truncated]";
}

void KillProcess(Process process)
{
    if (process.HasExited)
        return;

    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
        // Proces zakończył się pomiędzy HasExited a Kill.
    }
}

bool ToolMatches(string toolName, params string[] names)
{
    var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
    return names.Any(name =>
        normalized == name ||
        normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
        normalized.EndsWith($":{name}", StringComparison.Ordinal));
}

IReadOnlyDictionary<string, AllowedCliCommand> BuildAllowedCliCommands()
{
    var listFiles = OperatingSystem.IsWindows()
        ? new AllowedCliCommand("list_files", "Get-ChildItem -Name", "powershell.exe", ["-NoLogo", "-NoProfile", "-Command", "Get-ChildItem -Name"])
        : new AllowedCliCommand("list_files", "ls -1", "/bin/ls", ["-1"]);

    var listAllFiles = OperatingSystem.IsWindows()
        ? new AllowedCliCommand("list_all_files", "Get-ChildItem -Force | Select-Object -ExpandProperty Name", "powershell.exe", ["-NoLogo", "-NoProfile", "-Command", "Get-ChildItem -Force | Select-Object -ExpandProperty Name"])
        : new AllowedCliCommand("list_all_files", "ls -la", "/bin/ls", ["-la"]);

    AllowedCliCommand[] commands =
    [
        new("dotnet_info", "dotnet --info", "dotnet", ["--info"]),
        new("dotnet_packages", "dotnet list package --include-transitive", "dotnet", ["list", "package", "--include-transitive"]),
        new("git_log", "git --no-pager log --oneline --graph -10", "git", ["--no-pager", "log", "--oneline", "--graph", "-10"]),
        listFiles,
        listAllFiles,
    ];

    return commands.ToDictionary(command => command.Id, StringComparer.OrdinalIgnoreCase);
}

internal sealed record CliCommandResult(
    string Command,
    int? ExitCode,
    bool TimedOut,
    bool Rejected,
    bool Truncated,
    int RawCharacterCount,
    int SanitizedCharacterCount,
    int EstimatedTokensSaved,
    string SanitizedOutput);

internal sealed record AllowedCliCommand(
    string Id,
    string DisplayCommand,
    string FileName,
    string[] Arguments);

internal sealed record BoundedOutput(
    string Output,
    int TotalChars,
    bool Truncated);
