// Demo 09 — Wznowienie sesji: Asystent migracji przyrostowej
//
// Pokazuje: CopilotSession.SessionId + ResumeSessionAsync + trwały stan między procesami.
// Scenariusz: Wieloetapowa migracja .NET 6 → .NET 8. Run 1 analizuje projekt i zapisuje plan.
// Run 2 wznawia sesję — model pamięta co już przemigrował.
// Uruchomienie: dotnet run --project demos/09-session-resume/SessionResume [--resume]

using System.Text.Json;
using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

var JsonOptions = new JsonSerializerOptions { WriteIndented = true };

ConsoleRenderer.Banner("Session Resume", "Demo 09 — GitHub Copilot SDK: Incremental Migration Assistant");

const string StateFile = ".copilot-migration-session.json";
var isResume = args.Contains("--resume");

// Sprawdza, czy poprzednie uruchomienie zapisało identyfikator sesji SDK. Plik przechowuje
// metadane postępu demo; faktyczna historia rozmowy jest własnością środowiska
// wykonawczego Copilota i jest adresowana przez SessionId.
MigrationState? savedState = null;
if (File.Exists(StateFile))
{
    try
    {
        var json = await File.ReadAllTextAsync(StateFile);
        savedState = JsonSerializer.Deserialize<MigrationState>(json, JsonOptions);
    }
    catch
    {
        ConsoleRenderer.Warn("Nie można odczytać pliku stanu. Zaczynam od nowa.");
    }
}

// Ten sam typ klienta jest używany zarówno dla nowych, jak i wznowionych sesji. Klient
// jest właścicielem obsługi sesji; wznowienie nie wymaga specjalnego CopilotClient.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId();
var provider = CopilotClientFactory.GetByokProvider();

// Przykładowy projekt do migracji — osadzony w demie
var sampleProject = new SampleProject(
    Name: "ECommerce.Api",
    TargetFramework: "net6.0",
    Files:
    [
        new("Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddControllers();
            // Minimal hosting — wymaga aktualizacji do net8 pattern
            var app = builder.Build();
            app.MapControllers();
            app.Run();
            """),
        new("Controllers/OrdersController.cs", """
            [ApiController, Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult GetOrders() => Ok(new[] { "order1", "order2" });
            }
            """),
        new("Services/EmailService.cs", """
            public class EmailService
            {
                // Używa przestarzałego SmtpClient (net6 pattern)
                public void Send(string to, string subject, string body)
                {
                    using var client = new System.Net.Mail.SmtpClient("smtp.example.com");
                    client.Send("from@example.com", to, subject, body);
                }
            }
            """),
        new("appsettings.json", """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "AllowedHosts": "*",
              "ConnectionStrings": { "Default": "Server=localhost;Database=ecommerce;" }
            }
            """),
    ]
);

if (savedState is not null && !isResume)
{
    // Pokazuje co zostało zapisane z poprzedniej sesji
    ConsoleRenderer.Success($"Znaleziono zapisaną sesję migracji: {savedState.SessionId}");
    AnsiConsole.MarkupLine($"[grey]Projekt: {savedState.ProjectName}[/]");
    AnsiConsole.MarkupLine($"[grey]Przemigrowane pliki: {string.Join(", ", savedState.MigratedFiles)}[/]");
    AnsiConsole.MarkupLine($"[grey]Postęp: {savedState.Progress}%[/]");
    AnsiConsole.WriteLine();

    var choice = ConsoleRenderer.Prompt(
        $"Sesja {savedState.SessionId[..8]}... istnieje. Co chcesz zrobić? [resume/new/exit]");

    if (choice.Equals("exit", StringComparison.OrdinalIgnoreCase))
        return 0;

    if (choice.Equals("new", StringComparison.OrdinalIgnoreCase))
    {
        File.Delete(StateFile);
        savedState = null;
        ConsoleRenderer.Info("Usunięto poprzednią sesję. Zaczynam od nowa.\n");
    }
    else
    {
        isResume = true;
    }
}

if (isResume && savedState is not null)
{
    await RunPhase2Resume(client, model, provider, savedState, sampleProject);
}
else
{
    await RunPhase1Analysis(client, model, provider, sampleProject, StateFile);
}

return 0;

// ── FAZA 1: Analiza i plan migracji ──────────────────────────────────────────

async Task RunPhase1Analysis(
    CopilotClient client,
    string model,
    ProviderConfig? provider,
    SampleProject project,
    string stateFile)
{
    ConsoleRenderer.Rule("Faza 1: Analiza projektu i plan migracji");
    ConsoleRenderer.Info($"Analizuję projekt: {project.Name} ({project.TargetFramework} → net8.0)\n");

    // Narzędzie: list_project_files. Ta funkcja hosta tylko do odczytu daje modelowi
    // ograniczony spis przykładowego projektu, zamiast dowolnego dostępu do dysku.
    var listFilesTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        () => string.Join("\n", project.Files.Select(f => f.Path)),
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "list_project_files",
            Description = "Lists all files in the project that need to be migrated.",
        });

    // Narzędzie: read_project_file. SDK udostępnia ten schemat funkcji modelowi,
    // ale host sprawdza żądaną ścieżkę przed zwróceniem zawartości pliku.
    var readFileTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string path) =>
        {
            return TryFindProjectFile(project, path, out var file, out var normalizedReadPath, out var error)
                ? file.Content
                : $"ERROR: {error}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "read_project_file",
            Description = "Reads the content of a specific project file.",
        });

    // Narzędzie: mark_file_migrated. Pokazuje, że narzędzia mogą aktualizować stan hosta
    // podczas tury Copilota. Model wywołuje tę funkcję po wydaniu wskazówek dla pliku,
    // a host rejestruje postęp poza historią SDK.
    var migratedFiles = new List<string>();
    var markMigratedTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string path, string summary) =>
        {
            if (!TryGetKnownProjectPath(project, path, out var normalizedPath, out var error))
                return $"ERROR: {error}";

            if (!migratedFiles.Contains(normalizedPath, PathComparer()))
                migratedFiles.Add(normalizedPath);

            return $"Marked {normalizedPath} as migrated: {summary}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "mark_file_migrated",
            Description = "Marks a file as successfully migrated with a brief summary of changes made.",
        });

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        // Narzędzia są ograniczone do demo, a skutki uboczne ograniczają się do
        // listy migratedFiles w pamięci, więc żądania są zatwierdzane automatycznie.
        OnPermissionRequest = PermissionHandler.ApproveAll,
        // Narzędzia są częścią konfiguracji sesji. Ta pierwsza sesja ma
        // narzędzia analizy/postępu; wznowiona faza implementacji dostarcza później
        // inny zestaw narzędzi.
        Tools = [listFilesTool, readFileTool, markMigratedTool],
        // Komunikat systemowy określa, jak ta sesja CopilotSession interpretuje
        // wszystkie wyniki narzędzi i podpowiedzi użytkownika podczas analizy migracji.
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = """
                You are a .NET migration expert specializing in upgrading projects from .NET 6 to .NET 8.
                Your task is to analyze the project files and create a migration plan.

                Key .NET 8 migration patterns:
                - Update TargetFramework from net6.0 to net8.0
                - Use new minimal hosting model patterns (WebApplication.CreateBuilder is unchanged)
                - Replace System.Net.Mail.SmtpClient with MailKit (obsolete in .NET 8)
                - Enable nullable reference types
                - Use new IHostedService and BackgroundService patterns
                - Leverage new C# 12 features where beneficial

                Always use tools to read files before analyzing them.
                Use mark_file_migrated when you've provided migration guidance for a file.
                """,
        },
    });

    using var _ = EventLogger.Attach(session, verbose: false);

    // Krok 1 rozpoczyna normalną turę SDK. Model wywoła narzędzia w razie potrzeby,
    // pomocnik czeka na SessionIdleEvent, a ostatni komunikat asystenta staje się
    // planem migracji wyświetlonym poniżej.
    var analysisResult = await ConsoleRenderer.SpinnerAsync(
        "Analizuję pliki projektu...",
        () => SessionHelper.SendAndWaitAsync(session,
            $"""
            Analyze the {project.Name} project for .NET 8 migration.

            1. Call list_project_files to see all files
            2. Call read_project_file for each file
            3. Identify migration issues in each file (specific line-by-line changes needed)
            4. Prioritize files by migration complexity: SIMPLE/MEDIUM/COMPLEX
            5. For each file you've analyzed, call mark_file_migrated with a summary

            Format: For each file, show:
            - File: [path]
            - Complexity: [SIMPLE/MEDIUM/COMPLEX]
            - Issues found: [bullet list]
            - Required changes: [specific code changes]
            """));

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Plan migracji:[/]");
    AnsiConsole.WriteLine(analysisResult);

    // Środowisko wykonawcze SDK przypisuje identyfikator sesji. Zapisanie tej wartości pozwala
    // późniejszemu uruchomieniu wywołać ResumeSessionAsync i kontynuować tę samą rozmowę.
    var sessionId = session.SessionId;

    // Zapisuje tylko uchwyt do wznowienia i postęp po stronie hosta. Pełny monit,
    // wyniki narzędzi i komunikaty asystenta pozostają w sesji SDK/środowiska wykonawczego.
    var state = new MigrationState(
        SessionId: sessionId,
        ProjectName: project.Name,
        MigratedFiles: migratedFiles.ToList(),
        Progress: migratedFiles.Count * 100 / Math.Max(project.Files.Count, 1),
        Phase: "analysis_complete");

    await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(state, JsonOptions));

    AnsiConsole.WriteLine();
    ConsoleRenderer.Success($"Faza 1 zakończona. SessionId zapisany: {sessionId}");
    ConsoleRenderer.Info($"Przeanalizowane pliki: {string.Join(", ", migratedFiles)}");
    ConsoleRenderer.Info($"\nAby kontynuować migrację: dotnet run --project demos/09-session-resume/SessionResume --resume");
    ConsoleRenderer.Info("lub uruchom ponownie bez --resume aby zobaczyć opcję wznowienia.");
}

// ── FAZA 2: Wznowienie sesji i implementacja ─────────────────────────────────

async Task RunPhase2Resume(
    CopilotClient client,
    string model,
    ProviderConfig? provider,
    MigrationState savedState,
    SampleProject project)
{
    ConsoleRenderer.Rule("Faza 2: Wznowienie sesji — implementacja migracji");
    ConsoleRenderer.Success($"Wznawianie sesji {savedState.SessionId[..8]}...\n");

    // Narzędzie: generate_migrated_file. We wznowionej fazie ta funkcja hosta
    // przechwytuje wygenerowaną przez model pełną zawartość pliku w pamięci, zanim demo
    // zapisze ją do katalogu wyjściowego.
    var generatedFiles = new Dictionary<string, string>(PathComparer());
    var generateFileTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string path, string migratedContent) =>
        {
            if (!TryGetKnownProjectPath(project, path, out var normalizedPath, out var error))
                return $"ERROR: {error}";

            generatedFiles[normalizedPath] = migratedContent;
            return $"Generated migrated version of {normalizedPath} ({migratedContent.Length} chars)";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "generate_migrated_file",
            Description = "Saves the complete migrated version of a file. Call this with the full updated content.",
        });

    // Narzędzie: read_project_file jest dostarczane ponownie, ponieważ ResumeSessionAsync
    // przywraca historię konwersacji, a nie delegaty .NET z poprzedniego
    // procesu. Host musi ponownie zarejestrować narzędzia, których chce użyć we wznowionej sesji.
    var readFileTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(
        (string path) =>
        {
            return TryFindProjectFile(project, path, out var file, out var normalizedReadPath, out var error)
                ? file.Content
                : $"ERROR: {error}";
        },
        new Microsoft.Extensions.AI.AIFunctionFactoryOptions
        {
            Name = "read_project_file",
            Description = "Reads the original content of a project file.",
        });

    await using var resumedSession = await client.ResumeSessionAsync(savedState.SessionId,
        new ResumeSessionConfig
        {
            Model = model,
            Provider = provider,
            // ResumeSessionConfig ponownie dołącza zasady i możliwości hosta do
            // przywróconej rozmowy. Model pamięta fazę 1, ale wykonanie nadal
            // korzysta z narzędzi i uprawnień dostarczanych przez ten proces.
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [generateFileTool, readFileTool],
        });

    using var _ = EventLogger.Attach(resumedSession, verbose: false);

    ConsoleRenderer.Info("Sesja wznowiona — model pamięta poprzednią analizę.\n");

    // Prosi o prace implementacyjne we wznowionej sesji SDK. Monit może
    // odwoływać się do „twojej analizy”, ponieważ środowisko wykonawcze przywróciło poprzednie tury
    // według SessionId.
    var implementationResult = await ConsoleRenderer.SpinnerAsync(
        "Generuję zmigrowane pliki (model korzysta z poprzedniej analizy)...",
        () => SessionHelper.SendAndWaitAsync(resumedSession,
            $"""
            Based on your analysis, now generate the actual migrated versions of the files.

            For each file you analyzed:
            1. Call read_project_file to get the original content
            2. Apply all the .NET 8 migration changes you identified
            3. Call generate_migrated_file with the complete updated content

            Previously migrated files: {string.Join(", ", savedState.MigratedFiles)}
            Focus on files not yet fully migrated.

            Important: Generate complete, working code — not just diffs or descriptions.
            """));

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Wyniki migracji:[/]");
    AnsiConsole.WriteLine(implementationResult);

    // Pokaż wygenerowane pliki
    if (generatedFiles.Count > 0)
    {
        AnsiConsole.WriteLine();
        ConsoleRenderer.Rule("Wygenerowane zmigrowane pliki");

        foreach (var (path, content) in generatedFiles)
        {
            AnsiConsole.MarkupLine($"\n[bold cyan]📄 {path}[/]");
            AnsiConsole.WriteLine(content);
        }

        // Zapisz do katalogu wyjściowego
        var outputDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "MigratedProject"));
        Directory.CreateDirectory(outputDir);

        foreach (var (path, content) in generatedFiles)
        {
            if (!TryResolveOutputPath(outputDir, path, out var outputPath, out var error))
                throw new InvalidOperationException(error);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, content);
        }

        ConsoleRenderer.Success($"Zmigrowane pliki zapisane w: {outputDir}");
    }

    // Usuń plik stanu — migracja ukończona
    if (File.Exists(".copilot-migration-session.json"))
        File.Delete(".copilot-migration-session.json");

    AnsiConsole.WriteLine();
    ConsoleRenderer.Success("Migracja zakończona! Sesja usunięta z dysku.");
    ConsoleRenderer.Info("Demo pokazało: SessionId zapisany między procesami → ResumeSessionAsync → model zachował kontekst.");
}

static bool TryFindProjectFile(
    SampleProject project,
    string requestedPath,
    out ProjectFile file,
    out string normalizedPath,
    out string error)
{
    // Argumenty narzędzi są generowane przez model, więc każda ścieżka jest normalizowana i
    // dopasowywana do znanych przykładowych plików przed zwróceniem zawartości pliku.
    file = null!;
    if (!TryGetKnownProjectPath(project, requestedPath, out normalizedPath, out error))
        return false;

    var knownPath = normalizedPath;
    file = project.Files.First(f => PathComparer().Equals(NormalizeProjectPath(f.Path), knownPath));
    return true;
}

static bool TryGetKnownProjectPath(
    SampleProject project,
    string requestedPath,
    out string normalizedPath,
    out string error)
{
    normalizedPath = string.Empty;
    error = string.Empty;

    if (!TryNormalizeRelativePath(requestedPath, out var candidatePath, out error))
        return false;

    var knownPath = project.Files
        .Select(file => NormalizeProjectPath(file.Path))
        .FirstOrDefault(path => PathComparer().Equals(path, candidatePath));

    if (knownPath is null)
    {
        error = $"File '{requestedPath}' is not part of the sample project.";
        return false;
    }

    normalizedPath = knownPath;
    return true;
}

static bool TryResolveOutputPath(
    string outputDir,
    string projectPath,
    out string outputPath,
    out string error)
{
    // Narzędzie generujące pliki przechowuje zawartość w pamięci, ale ostateczny zapis
    // nadal sprawdza ścieżki przed utrwaleniem nazw plików wygenerowanych przez model.
    outputPath = string.Empty;
    if (!TryNormalizeRelativePath(projectPath, out var normalizedPath, out error))
        return false;

    outputPath = Path.GetFullPath(Path.Combine(outputDir, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
    if (!IsUnderDirectory(outputPath, outputDir))
    {
        error = $"Generated file path '{projectPath}' resolves outside '{outputDir}'.";
        return false;
    }

    return true;
}

static bool TryNormalizeRelativePath(string path, out string normalizedPath, out string error)
{
    normalizedPath = string.Empty;
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(path))
    {
        error = "File path is required.";
        return false;
    }

    if (path.Contains('\0') || Path.IsPathRooted(path))
    {
        error = "Only relative project file paths are allowed.";
        return false;
    }

    normalizedPath = NormalizeProjectPath(path);
    if (normalizedPath.Split('/').Any(segment => segment is "" or "." or ".."))
    {
        error = "Path traversal is not allowed.";
        return false;
    }

    return true;
}

static string NormalizeProjectPath(string path) =>
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

// ── typy ─────────────────────────────────────────────────────────────────────

record MigrationState(
    string SessionId,
    string ProjectName,
    List<string> MigratedFiles,
    int Progress,
    string Phase);

record SampleProject(string Name, string TargetFramework, List<ProjectFile> Files);

record ProjectFile(string Path, string Content);
