// Demo 06 — SecurityAudit: OWASP Top 10 Analyzer
//
// Pokazuje: SystemMessage z rolą eksperta bezpieczeństwa, multi-turn analiza pliku po pliku,
// własny tool ReadSourceFile, generowanie raportu JSON z kategoriami OWASP.
// Input: katalog SampleVulnerableCode/ (dostarczone w repo) lub własny katalog.
// Uruchomienie: dotnet run --project demos/06-security-audit/SecurityAudit [katalog]

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Security Audit", "Demo 06 — GitHub Copilot SDK: OWASP Top 10 Analyzer");

var targetDir = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SampleVulnerableCode");

targetDir = Path.GetFullPath(targetDir);

if (!Directory.Exists(targetDir))
    throw new DirectoryNotFoundException($"Katalog nie istnieje: {targetDir}");

var csFiles = Directory.GetFiles(targetDir, "*.cs", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();
var discoveredFiles = csFiles.ToDictionary(
    f => NormalizeRelativePath(Path.GetRelativePath(targetDir, f)),
    Path.GetFullPath,
    PathComparer());
ConsoleRenderer.Info($"Analizuję {csFiles.Count} plików w: {targetDir}\n");

// CopilotClient to współdzielona infrastruktura środowiska wykonawczego SDK. Zachowanie
// audytu bezpieczeństwa jest konfigurowane na poziomie sesji, ponieważ każdy audyt może mieć
// inny katalog docelowy, zestaw narzędzi i komunikat systemowy.
await using var client = CopilotClientFactory.Create();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    // Jedyne niestandardowe narzędzie jest tylko do odczytu i ma ograniczoną ścieżkę (poniżej),
    // więc demo zatwierdza prośby o uprawnienia pakietu SDK, aby skupić się na przepływie pracy audytu.
    OnPermissionRequest = PermissionHandler.ApproveAll,

    // Zastąpienie domyślnego komunikatu systemowego czyni z tej sesji wyspecjalizowanego
    // recenzenta bezpieczeństwa. Instrukcja jest przekazywana w każdej turze
    // CopilotSession i wskazuje, w jaki sposób model interpretuje wyniki narzędzi.
    SystemMessage = new SystemMessageConfig
    {
        Mode = SystemMessageMode.Replace,
        Content = """
        You are a senior application security engineer specializing in .NET/C# code review.

        Your task: Analyze C# source files for security vulnerabilities categorized by OWASP Top 10 2021.

        For each finding, provide:
        - file: filename
        - line: approximate line number
        - category: OWASP category (e.g., "A01:2021 - Broken Access Control")
        - severity: CRITICAL | HIGH | MEDIUM | LOW
        - title: short vulnerability name
        - description: what the vulnerability is
        - remediation: specific C# code fix

        Focus on: SQL Injection, hardcoded credentials, weak crypto, missing auth, path traversal,
        XXE, insecure deserialization, SSRF, timing attacks, logging sensitive data.

        Output ONLY valid JSON in the format:
        {
          "summary": {"critical": N, "high": N, "medium": N, "low": N},
          "findings": [{...}, ...]
        }
        """,
    },

    // Narzędzia udostępniają modelowi możliwości hosta. Model nie może odczytać
    // systemu plików bezpośrednio; musi wywołać read_source_file, a kod hosta
    // poniżej sprawdza, czy żądany plik należy do wykrytego zestawu audytu,
    // zanim zwróci tekst źródłowy.
    Tools =
    [
        AIFunctionFactory.Create(
            ([Description("Path to the C# source file to read")] string filePath) =>
            {
                if (!TryResolveDiscoveredFile(filePath, targetDir, discoveredFiles, out var fullPath, out var relativePath, out var error))
                    return $"ERROR: {error}";

                var content = File.ReadAllText(fullPath);
                return $"FILE: {relativePath}\nLINES: {content.Split('\n').Length}\n\n{content}";
            },
            "read_source_file",
            // SDK wysyła nazwę/opis/schemat narzędzia do modelu.
            // Precyzyjne opisy pomagają modelowi wybrać narzędzie, gdy potrzebuje
            // zawartości pliku, zamiast zgadywać na podstawie nazw plików.
            "Reads a C# source file for security analysis"),
    ],
});

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Analiza plików");

// Pokazuje wywołania narzędzi i postęp. ToolExecutionStartEvent jest emitowany przez SDK,
// gdy model wybrał read_source_file do wywołania i podał argumenty; funkcja hosta
// uruchamia się następnie w tym procesie.
var toolCallCount = 0;
using var events = session.On<SessionEvent>(evt =>
{
    if (evt is ToolExecutionStartEvent tool)
    {
        toolCallCount++;
        AnsiConsole.MarkupLine($"[yellow]⚙[/] Czytam: {tool.Data.ToolName.Replace("[", "[[").Replace("]", "]]")}...");
    }
});

var fileList = string.Join("\n", discoveredFiles.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => $"- {f}"));
var prompt = $"""
Analyze these C# files for OWASP Top 10 security vulnerabilities.
Use the read_source_file tool to read each file, then analyze it.

Files to analyze:
{fileList}

After reading all files, return the complete security report as JSON.
""";

var reportJson = await ConsoleRenderer.SpinnerAsync(
    "Copilot analizuje pod kątem OWASP Top 10...",
    // SessionHelper wysyła monit przez SDK i czeka na zdarzenie
    // SessionIdle. Podczas tej tury model może wywołać read_source_file
    // wielokrotnie, a każdy wynik jest dodawany z powrotem do rozmowy.
    () => SessionHelper.SendAndWaitAsync(session, prompt));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule($"Raport (wywołania narzędzi: {toolCallCount})");

// Parsuj i wyświetl raport
try
{
    // Wyodrębnij JSON z odpowiedzi (model może dodać tekst przed/po JSON)
    var jsonStart = reportJson.IndexOf('{');
    var jsonEnd = reportJson.LastIndexOf('}') + 1;
    if (jsonStart >= 0 && jsonEnd > jsonStart)
    {
        var jsonOnly = reportJson[jsonStart..jsonEnd];
        var report = JsonSerializer.Deserialize<SecurityReport>(jsonOnly,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (report?.Summary != null)
        {
            // Podsumowanie
            var severityTable = new[]
            {
                ("CRITICAL", report.Summary.Critical.ToString(), "bold red"),
                ("HIGH",     report.Summary.High.ToString(),     "red"),
                ("MEDIUM",   report.Summary.Medium.ToString(),   "yellow"),
                ("LOW",      report.Summary.Low.ToString(),      "blue"),
            };

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Podsumowanie:[/]");
            foreach (var (sev, count, color) in severityTable)
                AnsiConsole.MarkupLine($"  [{color}]{sev}:[/] {count}");

            // Tabela znalezisk
            if (report.Findings?.Count > 0)
            {
                AnsiConsole.WriteLine();
                ConsoleRenderer.Table(
                    report.Findings,
                    ("Plik", f => f.File ?? "?"),
                    ("Linia", f => f.Line.ToString()),
                    ("Severity", f => f.Severity ?? "?"),
                    ("Kategoria OWASP", f => (f.Category ?? "?").Length > 25 ? (f.Category ?? "?")[..25] + "…" : (f.Category ?? "?")),
                    ("Podatność", f => (f.Title ?? "?").Length > 35 ? (f.Title ?? "?")[..35] + "…" : (f.Title ?? "?")));
            }
        }

        // Zapisz raport
        var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "security-report.json");
        await File.WriteAllTextAsync(outputPath, jsonOnly);
        AnsiConsole.WriteLine();
        ConsoleRenderer.Success($"Raport zapisany: {outputPath}");
    }
    else
    {
        // Model nie zwrócił JSON — wyświetl surowy wynik
        AnsiConsole.WriteLine(reportJson);
    }
}
catch (JsonException)
{
    AnsiConsole.WriteLine(reportJson);
}

static bool TryResolveDiscoveredFile(
    string requestedPath,
    string targetDir,
    IReadOnlyDictionary<string, string> discoveredFiles,
    out string fullPath,
    out string relativePath,
    out string error)
{
    // Ta walidacja chroni granicę narzędzia. Ponieważ argumenty narzędzi to
    // JSON wygenerowany przez model, host musi wymusić, aby odczytać można było
    // tylko wcześniej wykryte ścieżki względne w katalogu audytu.
    fullPath = string.Empty;
    relativePath = string.Empty;
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(requestedPath))
    {
        error = "File path is required.";
        return false;
    }

    if (requestedPath.Contains('\0') || Path.IsPathRooted(requestedPath))
    {
        error = "Only relative paths from the audit file list are allowed.";
        return false;
    }

    relativePath = NormalizeRelativePath(requestedPath);
    if (relativePath.Split('/').Any(segment => segment is "." or ".." or ""))
    {
        error = "Path traversal is not allowed.";
        return false;
    }

    if (!discoveredFiles.TryGetValue(relativePath, out var discoveredFullPath))
    {
        error = $"File '{requestedPath}' is not in the discovered audit file set.";
        return false;
    }

    fullPath = Path.GetFullPath(Path.Combine(targetDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!IsUnderDirectory(fullPath, targetDir) || !PathComparer().Equals(fullPath, discoveredFullPath))
    {
        error = $"File '{requestedPath}' resolves outside the audit target.";
        return false;
    }

    return true;
}

static string NormalizeRelativePath(string path) =>
    path.Replace(Path.DirectorySeparatorChar, '/')
        .Replace(Path.AltDirectorySeparatorChar, '/');

static bool IsUnderDirectory(string path, string directory)
{
    var relative = Path.GetRelativePath(directory, path);
    return relative == "." ||
           (!relative.StartsWith("..", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative));
}

static StringComparer PathComparer() =>
    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

record SecurityReport(
    [property: JsonPropertyName("summary")] SecuritySummary? Summary,
    [property: JsonPropertyName("findings")] List<SecurityFinding>? Findings);

record SecuritySummary(
    [property: JsonPropertyName("critical")] int Critical,
    [property: JsonPropertyName("high")] int High,
    [property: JsonPropertyName("medium")] int Medium,
    [property: JsonPropertyName("low")] int Low);

record SecurityFinding(
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("remediation")] string? Remediation);
