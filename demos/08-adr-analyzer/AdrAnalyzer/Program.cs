// Demo 08 — AdrAnalyzer: recenzent decyzji dotyczących architektury
//
// Pokazuje: multi-turn session + tool ReadFile + ResumeSessionAsync.
// SDK analizuje 4 ADR dokumenty, wykrywa konflikty, blokujące decyzje i luki.
// Uruchomienie: dotnet run --project demos/08-adr-analyzer/AdrAnalyzer [katalog-z-adr]

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("ADR Analyzer", "Demo 08 — GitHub Copilot SDK: Architecture Decision Reviewer");
ConsoleRenderer.Info("Analizuje konflikty, blokujące decyzje i luki w zestawie ADR dokumentów.\n");

var adrDirectory = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "SampleAdrs");
adrDirectory = Path.GetFullPath(adrDirectory);

if (!Directory.Exists(adrDirectory))
{
    ConsoleRenderer.Error($"Katalog ADR nie istnieje: {adrDirectory}");
    return 1;
}

var adrFiles = Directory.GetFiles(adrDirectory, "*.md", SearchOption.TopDirectoryOnly)
    .OrderBy(f => f)
    .ToArray();
var adrFilesByName = adrFiles.ToDictionary(
    f => Path.GetFileName(f) ?? throw new InvalidOperationException($"ADR path has no file name: {f}"),
    Path.GetFullPath,
    PathComparer());

if (adrFiles.Length == 0)
{
    ConsoleRenderer.Error("Brak plików *.md w katalogu ADR.");
    return 1;
}

ConsoleRenderer.Info($"Znaleziono {adrFiles.Length} plików ADR w: {adrDirectory}");
foreach (var file in adrFiles)
    AnsiConsole.MarkupLine($"  [grey]• {Path.GetFileName(file)}[/]");
AnsiConsole.WriteLine();

// Jeden klient jest właścicielem połączenia ze środowiskiem wykonawczym SDK dla obu faz. Pierwsza faza
// tworzy sesję; druga faza wznawia tę samą sesję SDK według identyfikatora, dzięki czemu
// można kontynuować wcześniejszą analizę ADR.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId(/*"gpt-5.5"*/);
var provider = CopilotClientFactory.GetByokProvider();

// Narzędzie: read_adr_file. Ta funkcja hosta jest udostępniana modelowi przez
// SDK jako nazwane narzędzie. Model decyduje, kiedy je wywołać, ale host nadal
// sprawdza, czy żądana nazwa ADR należy do wykrytych plików.
var readAdrTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
    (string filename) =>
    {
        if (!TryResolveAdrFile(filename, adrDirectory, adrFilesByName, out var fullPath, out var error))
            return $"ERROR: {error}";

        return File.ReadAllText(fullPath);
    },
    new Microsoft.Extensions.AI.AIFunctionFactoryOptions
    {
        Name = "read_adr_file",
        // Opisy narzędzi są częścią schematu wysyłanego przez SDK do modelu;
        // powinny opisywać możliwości i ograniczenia, a nie implementację.
        Description = "Reads an ADR (Architecture Decision Record) file by filename. Returns full markdown content.",
    });

// Narzędzie: list_adr_files. Daje modelowi bezpieczny krok odkrywania, zamiast
// zakładać, że może przeglądać system plików. Zwracanie samych nazw plików utrzymuje też
// monit małym, dopóki model nie poprosi o odczytanie konkretnego ADR.
var listAdrsTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
    () => string.Join("\n", adrFilesByName.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)),
    new Microsoft.Extensions.AI.AIFunctionFactoryOptions
    {
        Name = "list_adr_files",
        Description = "Lists all available ADR files in the repository.",
    });

// === FAZA 1: Analiza ===
ConsoleRenderer.Rule("Faza 1: Wczytywanie i analiza ADR");

string sessionId;
AdrAnalysisResult? analysis = null;

await using (var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = model,
    Provider = provider,
    // Wywołania narzędzi to operacje SDK z bramką uprawnień. Narzędzia tego demo są
    // tylko do odczytu i ograniczone do folderu ADR, więc zatwierdzanie żądań
    // pozwala płynnie prowadzić analizę zarządzania.
    OnPermissionRequest = PermissionHandler.ApproveAll,
    // Te same instancje narzędzi są dołączone do tej sesji oraz do wznowionej
    // sesji poniżej. Narzędzia to konfiguracja sesji, a nie globalny stan klienta.
    Tools = [readAdrTool, listAdrsTool],
    // Tryb Replace zmienia tę sesję CopilotSession w specjalistę ds. zarządzania ADR.
    // Instrukcja pozostaje aktywna w obu turach tej pierwszej fazy.
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are a senior software architect specializing in architecture governance and decision management.
            Your task is to analyze a set of Architecture Decision Records (ADRs) and identify:
            1. Conflicts between decisions (contradictory requirements or incompatible choices)
            2. Blocking dependencies (ADR A cannot be implemented until ADR B is resolved)
            3. Undocumented gaps (something is referenced but has no ADR)
            4. Proposed/unresolved ADRs that block accepted ones
            5. Cross-cutting concerns not addressed (security, performance, observability)

            Always use the provided tools to read ADR files before analyzing them.
            Be specific: cite ADR numbers and exact sections when identifying issues.
            Output structured JSON when asked for structured output.
            """,
    },
}))
{
    // EventLogger pokazuje aktywność SDK, taką jak wywołania narzędzi, komunikaty asystenta i
    // zdarzenia bezczynności w trakcie wykonywania analizy ADR.
    using var _ = EventLogger.Attach(session, verbose: false);

    // Krok 1 prosi model o zaplanowanie użycia narzędzi. Aplikacja nie iteruje
    // sama po plikach; Copilot używa list_adr_files i read_adr_file przez
    // protokół wywoływania funkcji SDK.
    var step1Result = await ConsoleRenderer.SpinnerAsync(
        "Czytam i analizuję ADR dokumenty...",
        () => SessionHelper.SendAndWaitAsync(session,
            $"""
            Please analyze all ADR files in the repository.

            Step 1: Call list_adr_files to see what's available.
            Step 2: Call read_adr_file for EACH file to read its content.
            Step 3: After reading all files, provide a comprehensive analysis.

            Structure your analysis as follows:

            ## KONFLIKTY
            List each conflict between ADRs with: which ADRs conflict, what the conflict is, severity (HIGH/MEDIUM/LOW).

            ## BLOKUJĄCE ZALEŻNOŚCI
            List ADRs that block other ADRs from being implemented.

            ## LUKI I NIEUDOKUMENTOWANE DECYZJE
            List things referenced across ADRs that have no ADR themselves.

            ## PODSUMOWANIE
            Overall health of the ADR set (CRITICAL/WARNING/OK) and top 3 recommended actions.
            """));

    // SessionId jest przypisywany przez środowisko wykonawcze SDK. Zapisanie go lub przekazanie do
    // ResumeSessionAsync to sposób, w jaki późniejszy proces/faza ponownie łączy się z tą
    // historią rozmowy.
    sessionId = session.SessionId;
    ConsoleRenderer.Success($"Analiza zakończona. SessionId: {sessionId}");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Wyniki analizy:[/]");
    AnsiConsole.WriteLine(step1Result);
    AnsiConsole.WriteLine();

    // Krok 2 to druga tura w tej samej sesji. Model może opierać się na treści
    // ADR i analizie już obecnych w historii konwersacji SDK.
    var jsonResult = await ConsoleRenderer.SpinnerAsync(
        "Generuję strukturyzowany raport JSON...",
        () => SessionHelper.SendAndWaitAsync(session,
            """
            Now output the same analysis as a JSON object with this exact structure:
            {
              "summary": { "health": "CRITICAL|WARNING|OK", "total_adrs": N, "conflicts": N, "blockers": N, "gaps": N },
              "conflicts": [{ "id": "C1", "adrs": ["ADR-001", "ADR-002"], "description": "...", "severity": "HIGH|MEDIUM|LOW" }],
              "blockers": [{ "blocked_adr": "ADR-001", "blocking_adr": "ADR-004", "reason": "..." }],
              "gaps": [{ "id": "G1", "referenced_in": ["ADR-002"], "description": "...", "suggested_adr": "ADR-005" }],
              "recommendations": ["...", "...", "..."]
            }
            Output ONLY the JSON, no markdown fences, no explanation.
            """));

    // Parsuj JSON
    try
    {
        var cleanJson = ExtractJson(jsonResult);
        analysis = JsonSerializer.Deserialize<AdrAnalysisResult>(cleanJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }
    catch (Exception ex)
    {
        ConsoleRenderer.Warn($"Nie udało się sparsować JSON: {ex.Message}");
    }
}

// === FAZA 2: Resume sesji i deep-dive ===
ConsoleRenderer.Rule("Faza 2: Wznowienie sesji — szczegółowa analiza blokerów");
ConsoleRenderer.Info($"Wznawianie sesji {sessionId} (ResumeSessionAsync)...\n");

await using (var resumedSession = await client.ResumeSessionAsync(sessionId, new ResumeSessionConfig
{
    Model = model,
    Provider = provider,
    // ResumeSessionConfig odzwierciedla istotne ustawienia sesji. Środowisko wykonawcze
    // przywraca historię rozmów według identyfikatora, a host ponownie dostarcza narzędzia i
    // zasady uprawnień dla wznowionej interakcji.
    OnPermissionRequest = PermissionHandler.ApproveAll,
    Tools = [readAdrTool, listAdrsTool],
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
            You are a senior software architect specializing in architecture governance and decision management.
            Always use the provided tools to read ADR files before analyzing them.
            Be specific: cite ADR numbers and exact sections when identifying issues.
            """,
    },
}))
{
    using var _ = EventLogger.Attach(resumedSession, verbose: false);

    var deepDive = await ConsoleRenderer.SpinnerAsync(
        "Analiza głęboka: jak rozwiązać blokujące konflikty...",
        // Ponieważ jest to wznowiona sesja SDK, monit może odwoływać się do historii
        // bez ponownego wysyłania każdego pliku ADR w monicie.
        () => SessionHelper.SendAndWaitAsync(resumedSession,
            """
            Based on your earlier analysis of the ADRs, provide a concrete remediation plan:

            1. For the MOST CRITICAL conflict or blocker you found:
               - What exact decision needs to be made?
               - Who are the stakeholders that must participate?
               - What are the 2-3 options to consider?
               - What is your recommended resolution and why?

            2. Draft a brief outline for the missing ADR(s) you identified (ADR-005, etc.):
               - Title
               - Status: Proposed
               - Context (2-3 sentences)
               - Decision options (3 bullet points)

            Be specific and actionable. Reference the exact ADR sections where conflicts were found.
            """));

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold cyan]Plan naprawczy:[/]");
    AnsiConsole.WriteLine(deepDive);
}

// === PREZENTACJA WYNIKÓW ===
AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Podsumowanie analizy ADR");

if (analysis is not null)
{
    RenderSummaryTable(analysis);
}

// Zapisz raport
var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "adr-analysis-report.json");
if (analysis is not null)
{
    var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(reportPath, json, Encoding.UTF8);
    ConsoleRenderer.Success($"Raport JSON zapisany: {reportPath}");
}

ConsoleRenderer.Info("Demo pokazało: multi-turn session, ResumeSessionAsync, tool-based file reading.");
return 0;

// ── funkcje pomocnicze ──────────────────────────────────────────────────────────────────

static string ExtractJson(string text)
{
    var start = text.IndexOf('{');
    var end = text.LastIndexOf('}');
    if (start < 0 || end < 0) return text;
    return text[start..(end + 1)];
}

static bool TryResolveAdrFile(
    string filename,
    string adrDirectory,
    IReadOnlyDictionary<string, string> adrFilesByName,
    out string fullPath,
    out string error)
{
    // Argumenty narzędzia pochodzą z modelu, więc ten strażnik utrzymuje read_adr_file
    // ograniczone do wykrytych nazw plików w katalogu ADR, nawet jeśli model
    // poprosi o ścieżkę bezwzględną lub sekwencję przejścia katalogów.
    fullPath = string.Empty;
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(filename))
    {
        error = "Nazwa pliku ADR jest wymagana.";
        return false;
    }

    if (filename.Contains('\0') ||
        Path.IsPathRooted(filename) ||
        filename.Contains(Path.DirectorySeparatorChar) ||
        filename.Contains(Path.AltDirectorySeparatorChar) ||
        filename != Path.GetFileName(filename))
    {
        error = "Dozwolone są tylko nazwy plików ADR z listy.";
        return false;
    }

    if (!adrFilesByName.TryGetValue(filename, out var discoveredPath))
    {
        error = $"Plik ADR '{filename}' nie znajduje się na liście odkrytych plików.";
        return false;
    }

    fullPath = Path.GetFullPath(discoveredPath);
    if (!IsUnderDirectory(fullPath, adrDirectory))
    {
        error = $"Plik ADR '{filename}' rozwiązuje się poza katalog ADR.";
        return false;
    }

    return true;
}

static bool IsUnderDirectory(string path, string directory)
{
    var relative = Path.GetRelativePath(directory, path);
    return relative == "." ||
           (!relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative));
}

static StringComparer PathComparer() =>
    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

static void RenderSummaryTable(AdrAnalysisResult result)
{
    var healthColor = result.Summary?.Health switch
    {
        "CRITICAL" => "red",
        "WARNING" => "yellow",
        _ => "green",
    };

    AnsiConsole.MarkupLine($"[bold]Status zdrowia ADR: [{healthColor}]{result.Summary?.Health ?? "?"}[/][/]");
    AnsiConsole.WriteLine();

    if (result.Conflicts?.Count > 0)
    {
        var table = new Table().RoundedBorder().AddColumn("ID").AddColumn("ADR").AddColumn("Severity").AddColumn("Opis");
        foreach (var c in result.Conflicts)
        {
            var color = c.Severity switch { "HIGH" => "red", "MEDIUM" => "yellow", _ => "white" };
            table.AddRow(c.Id ?? "", string.Join(", ", c.Adrs ?? []), $"[{color}]{c.Severity}[/]", c.Description ?? "");
        }
        AnsiConsole.MarkupLine("[bold]Konflikty:[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    if (result.Blockers?.Count > 0)
    {
        var table = new Table().RoundedBorder().AddColumn("Zablokowane ADR").AddColumn("Blokujące ADR").AddColumn("Powód");
        foreach (var b in result.Blockers)
            table.AddRow(b.BlockedAdr ?? "", b.BlockingAdr ?? "", b.Reason ?? "");
        AnsiConsole.MarkupLine("[bold]Blokery:[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    if (result.Gaps?.Count > 0)
    {
        var table = new Table().RoundedBorder().AddColumn("ID").AddColumn("Brakujące ADR").AddColumn("Opis");
        foreach (var g in result.Gaps)
            table.AddRow(g.Id ?? "", g.SuggestedAdr ?? "", g.Description ?? "");
        AnsiConsole.MarkupLine("[bold]Luki:[/]");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    if (result.Recommendations?.Count > 0)
    {
        AnsiConsole.MarkupLine("[bold]Rekomendacje:[/]");
        for (int i = 0; i < result.Recommendations.Count; i++)
            AnsiConsole.MarkupLine($"  [cyan]{i + 1}.[/] {result.Recommendations[i]}");
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

record AdrAnalysisResult(
    [property: JsonPropertyName("summary")]
    AdrSummary? Summary,
    [property: JsonPropertyName("conflicts")]
    List<AdrConflict>? Conflicts,
    [property: JsonPropertyName("blockers")]
    List<AdrBlocker>? Blockers,
    [property: JsonPropertyName("gaps")]
    List<AdrGap>? Gaps,
    [property: JsonPropertyName("recommendations")]
    List<string>? Recommendations);

record AdrSummary(
    [property: JsonPropertyName("health")]
    string? Health,
    [property: JsonPropertyName("total_adrs")]
    int TotalAdrs,
    [property: JsonPropertyName("conflicts")]
    int Conflicts,
    [property: JsonPropertyName("blockers")]
    int Blockers,
    [property: JsonPropertyName("gaps")]
    int Gaps);

record AdrConflict(
    [property: JsonPropertyName("id")]
    string? Id,
    [property: JsonPropertyName("adrs")]
    List<string>? Adrs,
    [property: JsonPropertyName("description")]
    string? Description,
    [property: JsonPropertyName("severity")]
    string? Severity);

record AdrBlocker(
    [property: JsonPropertyName("blocked_adr")]
    string? BlockedAdr,
    [property: JsonPropertyName("blocking_adr")]
    string? BlockingAdr,
    [property: JsonPropertyName("reason")]
    string? Reason);

record AdrGap(
    [property: JsonPropertyName("id")]
    string? Id,
    [property: JsonPropertyName("referenced_in")]
    List<string>? ReferencedIn,
    [property: JsonPropertyName("description")]
    string? Description,
    [property: JsonPropertyName("suggested_adr")]
    string? SuggestedAdr);
