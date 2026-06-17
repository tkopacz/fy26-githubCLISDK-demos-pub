// Demo 19 — BYOK Local Code Review
//
// Pokazuje: Bring Your Own Key przez ProviderConfig oraz ten sam prompt mini code-review
// uruchamiany na providerze chmurowym, GitHub Models, OpenRouter albo lokalnym endpointcie Ollama.
//
// Skąd brać klucze:
// - OpenAI: https://platform.openai.com/api-keys
// - Anthropic: https://console.anthropic.com/settings/keys
// - Azure OpenAI / Azure AI Foundry: klucz zasobu w Azure Portal lub Azure AI Foundry
// - GitHub Models: token GitHub z minimalnymi uprawnieniami do inference; lokalnie najlepiej
//   fine-grained PAT z GitHub Developer settings, a w GitHub Actions użyj GITHUB_TOKEN.
// - OpenRouter: https://openrouter.ai/keys — używaj jako OpenAI-compatible endpoint.
//   GitHub Models, OpenRouter i Ollama traktujemy jako OpenAI-compatible endpoints, dlatego Type="openai".
//
// Modele lokalne dla Ollama:
// - Dell 64 GB RAM + NVIDIA A500 / słabsze GPU: qwen2.5-coder:7b-instruct
// - NVIDIA RTX 3090 24 GB: qwen2.5-coder:32b-instruct, fallback qwen2.5-coder:14b-instruct
//
// Uruchomienie:
//   dotnet run --project demos/19-byok-local-code-review/ByokLocalCodeReview -- cloud
//   dotnet run --project demos/19-byok-local-code-review/ByokLocalCodeReview -- github-models
//   dotnet run --project demos/19-byok-local-code-review/ByokLocalCodeReview -- openrouter
//   dotnet run --project demos/19-byok-local-code-review/ByokLocalCodeReview -- local
//   dotnet run --project demos/19-byok-local-code-review/ByokLocalCodeReview -- both

using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("BYOK Review", "Demo 19 — GitHub Copilot SDK: BYOK Local Code Review");

// Wczytaj zmienne z .env (jeśli istnieje) — istniejące env vars mają pierwszeństwo
DotEnvLoader.Load();

var mode = ParseMode(args);
PrintSetupHints();

DemoRun[] runs = mode switch
{
    DemoMode.Cloud => [CreateCloudRun()],
    DemoMode.GitHubModels => [CreateGitHubModelsRun()],
    DemoMode.OpenRouter => [CreateOpenRouterRun()],
    DemoMode.Local => [CreateLocalRun()],
    DemoMode.Both => [CreateCloudRun(), CreateLocalRun()],
    _ => throw new InvalidOperationException($"Unsupported mode: {mode}"),
};

foreach (var run in runs)
{
    ConsoleRenderer.Rule(run.Title);
    ConsoleRenderer.Info($"Model: {run.Model}");
    ConsoleRenderer.Info($"Provider: {DescribeProvider(run.Provider)}\n");

    try
    {
        var result = await RunReviewAsync(run);
        AnsiConsole.MarkupLine("[bold green]Wynik mini code-review:[/]\n");
        AnsiConsole.WriteLine(result);
    }
    catch (Exception ex) when (IsActionableRuntimeFailure(ex))
    {
        ConsoleRenderer.Error(ex.Message);
        PrintTroubleshooting(run.Mode);
    }

    AnsiConsole.WriteLine();
}

return 0;

static async Task<string> RunReviewAsync(DemoRun run)
{
    // CopilotClient to to samo połączenie środowiska wykonawczego, niezależnie od dostawcy.
    // To SessionConfig.Provider wybiera w tym konkretnym przypadku routing BYOK dla tej rozmowy.
    await using var client = CopilotClientFactory.Create();
    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        // Model musi pasować do wybranego dostawcy. W trybie hostowanym przez GitHub Copilot
        // jest to identyfikator modelu Copilot; w przypadku punktów końcowych BYOK zgodnych
        // z OpenAI jest to nazwa modelu/wdrożenia danego dostawcy.
        Model = run.Model,
        // Provider równy null oznacza „użyj routingu subskrypcji GitHub Copilot”.
        // ProviderConfig oznacza „wyślij wywołania modelu tej sesji do punktu końcowego
        // BYOK z dostarczonym kluczem API/bazowym adresem URL”.
        Provider = run.Provider,
        // W tej minirecenzji nie zarejestrowano żadnych narzędzi, więc zatwierdzanie wszystkiego
        // utrzymuje standardową powierzchnię uprawnień SDK prostą dla tego demo dostawcy.
        OnPermissionRequest = PermissionHandler.ApproveAll,
        SystemMessage = new SystemMessageConfig
        {
            // Tryb Append zachowuje domyślne instrukcje SDK/środowiska wykonawczego i dodaje
            // rolę recenzenta na górze tej sesji.
            Mode = SystemMessageMode.Append,
            Content = """
            You are a senior C#/.NET reviewer. Keep feedback practical, security-focused, and concise.
            Return exactly three findings and one improved code snippet.
            """,
        },
    });

    return await ConsoleRenderer.SpinnerAsync(
        $"Uruchamiam review przez {run.Title}...",
        // SendAndWaitAsync ukrywa pętlę zdarzeń, dzięki czemu porównanie dostawców
        // skupia się na zachowaniu modelu/dostawcy, a nie na mechanice streamingu.
        () => SessionHelper.SendAndWaitAsync(session, BuildReviewPrompt()));
}

static string BuildReviewPrompt() =>
    $$$"""
    Review this C# code. Find the three most important issues and propose a safer, production-ready version.

    ```csharp
    using System.Data.SqlClient;

    public sealed class InvoiceRepository
    {
        private readonly string _connectionString;

        public InvoiceRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public decimal GetInvoiceTotal(string invoiceId)
        {
            using var connection = new SqlConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Total FROM Invoices WHERE Id = '" + invoiceId + "'";

            var result = command.ExecuteScalar();
            return decimal.Parse(result!.ToString()!);
        }
    }
    ```
    """;

static DemoMode ParseMode(string[] args)
{
    if (args.Length == 0)
        return DemoMode.OpenRouter;

    return args[0].Trim().ToLowerInvariant() switch
    {
        "cloud" => DemoMode.Cloud,
        "github-models" => DemoMode.GitHubModels,
        "github" => DemoMode.GitHubModels,
        "openrouter" => DemoMode.OpenRouter,
        "router" => DemoMode.OpenRouter,
        "local" => DemoMode.Local,
        "both" => DemoMode.Both,
        _ => throw new ArgumentException("Tryb musi być jednym z: cloud, github-models, openrouter, local, both."),
    };
}

static DemoRun CreateCloudRun()
{
    // Tryb chmury wykorzystuje współdzielone zachowanie fabryki: albo normalny routing
    // GitHub Copilot, albo BYOK_MODE ze standardowych zmiennych środowiskowych.
    return new DemoRun(
        DemoMode.Cloud,
        "Cloud BYOK / GitHub Copilot",
        CopilotClientFactory.GetModelId(),
        CopilotClientFactory.GetByokProvider());
}

static DemoRun CreateGitHubModelsRun()
{
    // GitHub Models udostępnia interfejs API wnioskowania zgodny z OpenAI, więc SDK widzi
    // to jako ProviderConfig.Type="openai", mimo że usługą wspierającą jest GitHub Models.
    var token = FirstNonEmpty(
        Environment.GetEnvironmentVariable("GITHUB_MODELS_TOKEN"),
        Environment.GetEnvironmentVariable("BYOK_API_KEY"),
        Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("GitHub Models mode requires GITHUB_MODELS_TOKEN, BYOK_API_KEY, or GITHUB_TOKEN.");

    var model = FirstNonEmpty(
        Environment.GetEnvironmentVariable("GITHUB_MODELS_MODEL"),
        Environment.GetEnvironmentVariable("COPILOT_MODEL"),
        "openai/gpt-4.1-mini");

    var baseUrl = FirstNonEmpty(
        Environment.GetEnvironmentVariable("GITHUB_MODELS_BASE_URL"),
        "https://models.github.ai/inference");

    return new DemoRun(
        DemoMode.GitHubModels,
        "GitHub Models BYOK",
        model,
        new ProviderConfig
        {
            // ProviderConfig jest używany przez SessionConfig.Provider. SDK używa
            // tego punktu końcowego dla wywołań modelu tylko w tej sesji.
            Type = "openai",
            BaseUrl = baseUrl,
            ApiKey = token,
        });
}

static DemoRun CreateOpenRouterRun()
{
    // OpenRouter także udostępnia interfejs API zgodny z OpenAI. Ważna koncepcja SDK
    // jest taka, że BaseUrl i ApiKey podróżują wraz z ProviderConfig danej sesji.
    var apiKey = FirstNonEmpty(
        Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
        Environment.GetEnvironmentVariable("BYOK_API_KEY"));

    if (string.IsNullOrWhiteSpace(apiKey))
        throw new InvalidOperationException("OpenRouter mode requires OPENROUTER_API_KEY or BYOK_API_KEY.");

    var model = FirstNonEmpty(
        Environment.GetEnvironmentVariable("OPENROUTER_MODEL"),
        Environment.GetEnvironmentVariable("COPILOT_MODEL"),
        "openai/gpt-4o-mini");

    var baseUrl = FirstNonEmpty(
        Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL"),
        "https://openrouter.ai/api/v1");

    return new DemoRun(
        DemoMode.OpenRouter,
        "OpenRouter BYOK",
        model,
        new ProviderConfig
        {
            Type = "openai",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        });
}

static DemoRun CreateLocalRun()
{
    // Punkt końcowy /v1 Ollamy jest zgodny z OpenAI, dzięki czemu ten sam kształt
    // ProviderConfig z Copilot SDK kieruje tę sesję na lokalny model.
    var model = FirstNonEmpty(
        Environment.GetEnvironmentVariable("LOCAL_MODEL"),
        "qwen2.5-coder:7b-instruct");

    var baseUrl = FirstNonEmpty(
        Environment.GetEnvironmentVariable("LOCAL_OPENAI_BASE_URL"),
        "http://localhost:11434/v1");

    var apiKey = FirstNonEmpty(
        Environment.GetEnvironmentVariable("LOCAL_API_KEY"),
        "ollama-local-key");

    return new DemoRun(
        DemoMode.Local,
        "Local Ollama BYOK",
        model,
        new ProviderConfig
        {
            Type = "openai",
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        });
}

static string FirstNonEmpty(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

static string DescribeProvider(ProviderConfig? provider)
{
    if (provider is null)
        return "default GitHub Copilot provider";

    return $"{provider.Type} at {provider.BaseUrl}";
}

static void PrintSetupHints()
{
    ConsoleRenderer.Info("To samo zadanie mini code-review można uruchomić na chmurze i lokalnym modelu.");
    ConsoleRenderer.Info("Sekretów nie zapisuj w repo. Ustawiaj je w zmiennych środowiskowych, user-secrets, GitHub Actions secrets albo managerze sekretów.\n");

    AnsiConsole.MarkupLine("[bold]GitHub Models jako BYOK OpenAI-compatible:[/]");
    AnsiConsole.MarkupLine("  $env:BYOK_PROVIDER = 'openai'");
    AnsiConsole.MarkupLine("  $env:BYOK_BASE_URL = 'https://models.github.ai/inference'");
    AnsiConsole.MarkupLine("  $env:BYOK_API_KEY = '<GitHub fine-grained PAT albo GITHUB_TOKEN w Actions>'");
    AnsiConsole.MarkupLine("  $env:COPILOT_MODEL = 'openai/gpt-4.1-mini'\n");

    AnsiConsole.MarkupLine("[bold]OpenRouter jako BYOK OpenAI-compatible:[/]");
    AnsiConsole.MarkupLine("  Klucz utwórz na https://openrouter.ai/keys i trzymaj go poza repozytorium.");
    AnsiConsole.MarkupLine("  $env:OPENROUTER_API_KEY = '<openrouter-api-key>'");
    AnsiConsole.MarkupLine("  $env:OPENROUTER_MODEL = 'openai/gpt-4o-mini'");
    AnsiConsole.MarkupLine("  $env:OPENROUTER_BASE_URL = 'https://openrouter.ai/api/v1'\n");

    AnsiConsole.MarkupLine("[bold]Ollama lokalnie jako OpenAI-compatible endpoint:[/]");
    AnsiConsole.MarkupLine("  Ollama wystawia API zgodne z OpenAI Chat Completions, więc w BYOK używamy providera 'openai'.");
    AnsiConsole.MarkupLine("  ollama pull qwen2.5-coder:7b-instruct");
    AnsiConsole.MarkupLine("  $env:LOCAL_OPENAI_BASE_URL = 'http://localhost:11434/v1'");
    AnsiConsole.MarkupLine("  $env:LOCAL_MODEL = 'qwen2.5-coder:7b-instruct'");
    AnsiConsole.MarkupLine("  $env:LOCAL_API_KEY = 'ollama-local-key'\n");
}

static void PrintTroubleshooting(DemoMode mode)
{
    if (mode is DemoMode.Local)
    {
        ConsoleRenderer.Warn("Sprawdź czy Ollama działa: ollama serve");
        ConsoleRenderer.Warn("Pobierz model: ollama pull qwen2.5-coder:7b-instruct");
        ConsoleRenderer.Warn("Dla RTX 3090 możesz użyć: $env:LOCAL_MODEL = 'qwen2.5-coder:32b-instruct'");
        return;
    }

    if (mode is DemoMode.GitHubModels)
    {
        ConsoleRenderer.Warn("Ustaw GITHUB_MODELS_TOKEN albo BYOK_API_KEY tokenem GitHub z dostępem do GitHub Models inference.");
        ConsoleRenderer.Warn("W GitHub Actions preferuj GITHUB_TOKEN lub sekret repozytorium, nie zapisuj PAT w kodzie.");
        return;
    }

    if (mode is DemoMode.OpenRouter)
    {
        ConsoleRenderer.Warn("Ustaw OPENROUTER_API_KEY kluczem z https://openrouter.ai/keys albo użyj BYOK_API_KEY.");
        ConsoleRenderer.Warn("Model ustaw przez OPENROUTER_MODEL, np. 'openai/gpt-4o-mini'.");
        return;
    }

    ConsoleRenderer.Warn("Dla BYOK ustaw BYOK_MODE=1, BYOK_PROVIDER, BYOK_API_KEY oraz opcjonalnie BYOK_BASE_URL i COPILOT_MODEL.");
}

static bool IsActionableRuntimeFailure(Exception ex) =>
    ex is InvalidOperationException or HttpRequestException or TaskCanceledException or TimeoutException or ArgumentException;

internal enum DemoMode
{
    Cloud,
    GitHubModels,
    OpenRouter,
    Local,
    Both,
}

internal sealed record DemoRun(DemoMode Mode, string Title, string Model, ProviderConfig? Provider);
