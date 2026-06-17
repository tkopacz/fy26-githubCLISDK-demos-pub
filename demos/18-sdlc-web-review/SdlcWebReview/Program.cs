// Demo 18 — SdlcWebReview: Asystent sieciowy SDLC
//
// Pokazuje: GitHub Copilot SDK wywołany z własnej strony ASP.NET Core w kontekście SDLC.
// Analizuje Pull Request przez GitHub MCP Server, uruchamiając 3 wyspecjalizowane sesje
// Copilota (architektura, bezpieczeństwo, wydajność) i streamuje wyniki przez SSE do
// przeglądarki na żywo.
//
// Wymagania: GITHUB_TOKEN env var z dostępem do odczytu PR w analizowanym repo
// Uruchomienie: dotnet run --project demos/18-sdlc-web-review/SdlcWebReview
// Otwórz: http://localhost:5080

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using System.Diagnostics;

// Wczytaj zmienne z .env (jeśli istnieje) — istniejące env vars mają pierwszeństwo
DotEnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);
var securityOptions = SdlcWebReviewSecurityOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(securityOptions);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestLineSize = securityOptions.MaxRequestLineBytes;
});

// === Singleton CopilotClient ===
// CopilotClient jest właścicielem połączenia ze środowiskiem wykonawczym SDK dla procesu ASP.NET.
// Utrzymuj jedną instancję usługi aktywną dla hosta i twórz krótkotrwałe instancje CopilotSession
// na każdą perspektywę recenzji lub żądanie.
//
// Nie używaj bezpośrednio AddHostedService<CopilotService>(): spowodowałoby to utworzenie
// drugiej usługi CopilotService, a zatem drugiego cyklu życia klienta/środowiska wykonawczego SDK.
builder.Services.AddSingleton<CopilotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(securityOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            SdlcWebReviewSecurity.GetRateLimitPartitionKey(ctx, securityOptions),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = securityOptions.RateLimitPermitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromSeconds(securityOptions.RateLimitWindowSeconds),
            }));
});

var app = builder.Build();
app.UseCors();
app.UseRateLimiter();
app.Use((ctx, next) => SdlcWebReviewSecurity.RequireApiKeyAsync(ctx, next, securityOptions));

// ── GET / — Strona HTML z formularzem i EventSource JS ────────────────────────
app.MapGet("/", () => Results.Content(HtmlPage, "text/html"));

// ── GET /health ───────────────────────────────────────────────────────────────
app.MapGet("/health", async (CopilotService copilot, CancellationToken ct) =>
{
    var health = await copilot.CheckHealthAsync(ct);
    return Results.Json(new
    {
        status = health.IsHealthy ? "healthy" : "unhealthy",
        copilot_state = health.State,
        reason = health.Reason,
        timestamp = health.CheckedAtUtc,
    }, statusCode: health.IsHealthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

// ── GET /api/review/stream ────────────────────────────────────────────────────
// Punkt końcowy SSE. Results.ServerSentEvents przyjmuje IAsyncEnumerable<string>, co
// mapuje się naturalnie na mostek strumieniowy SDK używany w ReviewPrStreamAsync.
// Przeglądarka otrzymuje fragmenty z trzech wyspecjalizowanych sesji Copilot.
app.MapGet("/api/review/stream", (
    string owner,
    string repo,
    int pr,
    CopilotService copilot,
    SdlcWebReviewSecurityOptions security,
    CancellationToken ct) =>
{
    if (SdlcWebReviewSecurity.ValidateReviewRequest(owner, repo, pr, security) is { } validationError)
        return validationError;

    return Results.ServerSentEvents(copilot.ReviewPrStreamAsync(owner, repo, pr, ct));
});

app.Run();

// ── CopilotService ────────────────────────────────────────────────────────────

public sealed class CopilotService : IHostedService, IAsyncDisposable
{
    private CopilotClient? _client;
    private readonly string _model;
    private readonly GitHub.Copilot.ProviderConfig? _provider;
    private readonly string? _githubToken;
    private volatile bool _isConnected;
    private DateTimeOffset _lastHealthCheckUtc = DateTimeOffset.UtcNow;

    public bool IsConnected => _client is not null && _isConnected;

    public CopilotService(SdlcWebReviewSecurityOptions _)
    {
        _model = CopilotClientFactory.GetModelId("gpt-5.4");
        _provider = CopilotClientFactory.GetByokProvider();
        _githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Uruchom środowisko wykonawcze SDK raz podczas startu ASP.NET. Poszczególne żądania
        // recenzji PR nie powinny ponosić kosztu ani ryzyka tworzenia nowego klienta.
        _client = CopilotClientFactory.Create();
        await CheckHealthAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeClientAsync();
    }

    public async Task<CopilotHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        if (_client is null)
            return SetHealth(false, "Disconnected", "CopilotClient not started.");

        try
        {
            // Uwierzytelnianie to sprawdzenie SDK na poziomie klienta. Weryfikuje stan
            // środowiska wykonawczego/konta przed utworzeniem jakiejkolwiek sesji recenzji.
            var auth = await _client.GetAuthStatusAsync().WaitAsync(ct);
            if (!auth.IsAuthenticated)
                return SetHealth(false, "Unauthenticated", "Copilot runtime is reachable but not authenticated.");

            return SetHealth(true, "Connected", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SetHealth(false, "Unavailable", ex.Message);
        }
    }

    private CopilotHealth SetHealth(bool isHealthy, string state, string? reason)
    {
        _isConnected = isHealthy;
        _lastHealthCheckUtc = DateTimeOffset.UtcNow;
        return new CopilotHealth(isHealthy, state, reason, _lastHealthCheckUtc);
    }

    private async ValueTask DisposeClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _isConnected = false;

        if (client is not null)
            await client.DisposeAsync();
    }

    /// <summary>
    /// Główna metoda: 3 wyspecjalizowane sesje Copilota (sekwencyjnie) z GitHub MCP Server.
    /// Każda sesja ma inną personę i system prompt. Wyniki streamowane przez SSE.
    ///
    /// Wzorzec: Demo 12 (McpHttpServerConfig) + Demo 07 (wyspecjalizowane sesje)
    ///           + Demo 10 (SessionChannelBridge → IAsyncEnumerable)
    /// </summary>
    public async IAsyncEnumerable<string> ReviewPrStreamAsync(
        string owner,
        string repo,
        int pr,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null)
        {
            yield return "⚠️ CopilotClient nie jest gotowy. Spróbuj ponownie za chwilę.";
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_githubToken))
        {
            yield return "⚠️ Brak zmiennej środowiskowej GITHUB_TOKEN.\n";
            yield return "Ustaw: `$env:GITHUB_TOKEN = 'ghp_...'` i uruchom ponownie serwer.";
            yield break;
        }

        yield return $"# 🔍 Analiza PR #{pr} w repozytorium `{owner}/{repo}`\n\n";
        yield return $"> Uruchamiam 3 wyspecjalizowane sesje GitHub Copilot SDK...\n\n";

        // Konfiguracja serwera GitHub MCP. Ten słownik jest używany ponownie przez każdą
        // sesję perspektywiczną, dzięki czemu każdy recenzent otrzymuje ten sam zestaw narzędzi
        // GitHub MCP tylko do odczytu oraz ten sam nagłówek autoryzacji.
        var mcpConfig = new Dictionary<string, McpServerConfig>
        {
            ["github"] = new McpHttpServerConfig
            {
                Url = "https://api.githubcopilot.com/mcp/",
                Headers = new Dictionary<string, string>
                {
                    // SDK wysyła ten nagłówek do serwera MCP. Model widzi
                    // schematy/wyniki narzędzi, a nie sam token.
                    ["Authorization"] = $"Bearer {_githubToken}",
                },
                // Filtrowanie narzędzi uniemożliwia sesjom modyfikowanie stanu GitHuba,
                // jednocześnie pozwalając na pobieranie szczegółów PR, różnic i komentarzy.
                Tools = SdlcWebReviewSecurity.ReadOnlyGitHubTools,
            },
        };

        var perspectives = new[]
        {
            new ReviewPerspective(
                Emoji: "🏗️",
                Title: "ARCHITEKTURA",
                Persona: "Senior Software Architect",
                Focus: "SOLID violations, coupling, design patterns, DRY, testability, separation of concerns"),

            new ReviewPerspective(
                Emoji: "🔒",
                Title: "BEZPIECZEŃSTWO",
                Persona: "Application Security Specialist",
                Focus: "OWASP Top 10: injection flaws, broken authentication, exposed sensitive data, security misconfiguration, missing access control"),

            new ReviewPerspective(
                Emoji: "⚡",
                Title: "WYDAJNOŚĆ",
                Persona: ".NET Performance Engineer",
                Focus: "N+1 queries, .Result/.Wait() blocking calls, missing ConfigureAwait, memory allocations, missing async, inefficient LINQ"),
        };

        foreach (var p in perspectives)
        {
            yield return $"\n\n## {p.Emoji} {p.Title}\n\n";
            yield return $"_Sesja Copilot: {p.Persona} — pobieranie danych PR przez GitHub MCP..._\n\n";

            // Każda perspektywa to osobna sesja SDK z własnym komunikatem systemowym
            // i własnym strumieniem zdarzeń. Dzięki temu wnioski dotyczące architektury,
            // bezpieczeństwa i wydajności nie zanieczyszczają się nawzajem, zanim strona
            // internetowa wyświetli je sekwencyjnie.
            await foreach (var token in RunPerspectiveSessionAsync(p, owner, repo, pr, mcpConfig, ct))
                yield return token;
        }

        yield return $"\n\n---\n_Analiza wygenerowana przez GitHub Copilot SDK · Demo 18 SDLC Web Review_\n";
    }

    /// <summary>
    /// Tworzy jedną sesję Copilot z GitHub MCP i przesyła strumieniowo tekst asystenta
    /// przez <see cref="SessionChannelBridge"/>.
    /// </summary>
    private async IAsyncEnumerable<string> RunPerspectiveSessionAsync(
        ReviewPerspective perspective,
        string owner,
        string repo,
        int pr,
        Dictionary<string, McpServerConfig> mcpConfig,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // To jest podstawowa sesja SDK dla jednej perspektywy przeglądu. Model,
        // dostawca, zasady uprawnień, zestaw serwerów MCP i persona mają zasięg
        // sesji, dzięki czemu równoczesne żądania pozostają odizolowane.
        await using var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            Model = _model,
            Provider = _provider,
            // Wywołania GitHub MCP są bramkowane uprawnieniami. Strażnik zatwierdza wyłącznie
            // narzędzia z listy dozwolonych tylko do odczytu.
            OnPermissionRequest = SdlcWebReviewSecurity.ReadOnlyGitHubMcpPermissionAsync,
            // PreToolUse sprawdza konkretną nazwę narzędzia tuż przed tym,
            // zanim SDK przekaże wywołanie do serwera GitHub MCP.
            Hooks = new SessionHooks
            {
                OnPreToolUse = (input, _) =>
                {
                    if (input.ToolName == "report_intent" ||
                        SdlcWebReviewSecurity.IsAllowedReadOnlyGitHubTool(input.ToolName))
                    {
                        Console.WriteLine("Enabled: " + input.ToolName);
                        return Task.FromResult<PreToolUseHookOutput?>(null);

                    }

                    Console.WriteLine("Blocked - unexpected tool: " + input.ToolName);

                    return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
                    {
                        PermissionDecision = "deny",
                        AdditionalContext = "BLOCKED: Demo 18 allows only read-only GitHub MCP tools.",
                    });
                },
            },
            // Streaming sprawia, że SDK emituje fragmenty zdarzenia AssistantMessageDeltaEvent
            // dla aktualizacji przeglądarki o niskim opóźnieniu.
            Streaming = true,
            // Dołącz serwer HTTP MCP do tej sesji. Model musi używać narzędzi MCP
            // do pobierania danych PR; kod C# nie wywołuje GitHuba bezpośrednio.
            McpServers = mcpConfig,
            SystemMessage = new SystemMessageConfig
            {
                // Tryb zamiany nadaje tej sesji czystą personę recenzenta
                // odpowiadającą wybranej perspektywie.
                Mode = SystemMessageMode.Replace,
                Content = $"""
                    You are a {perspective.Persona} reviewing a GitHub pull request.
                    Focus exclusively on: {perspective.Focus}.
                    Use GitHub MCP tools to read the actual PR data before writing your review.
                    Format your response as Markdown. Be concise and actionable — max 350 words.
                    Start with 1-2 sentence summary, then bullet points with specific findings.
                    """,
            },
        }, ct);

        // SDK używa wywołań zwrotnych do strumieniowania zdarzeń; mostek adaptuje te
        // zdarzenia do IAsyncEnumerable<string> dla zdarzeń wysyłanych przez serwer.
        using var bridge = new SessionChannelBridge();
        bridge.Attach(session);

        // SendAsync rozpoczyna turę. Metoda następnie zwraca fragmenty z
        // mostka, dopóki SessionIdleEvent nie zamknie strumienia.
        await session.SendAsync(new MessageOptions
        {
            Prompt = $"""
                Review PR #{pr} in {owner}/{repo} from your specialized perspective.

                Steps:
                1. Use GitHub MCP to get PR details (title, description, author, status)
                2. Get the list of changed files and their diffs
                3. Apply your specialized review lens: {perspective.Focus}
                4. List specific findings with file names and line numbers where relevant

                Focus only on your area of expertise. Be specific and actionable.
                """,
        }, ct);

        await foreach (var token in bridge.ReadAllAsync(ct))
            yield return token;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

record ReviewPerspective(string Emoji, string Title, string Persona, string Focus);

public sealed record CopilotHealth(
    bool IsHealthy,
    string State,
    string? Reason,
    DateTimeOffset CheckedAtUtc);

// ── HTML Page (inline) ────────────────────────────────────────────────────────

static partial class Program
{
    private const string HtmlPage = """
        <!DOCTYPE html>
        <html lang="pl">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>🤖 Copilot SDLC — PR Review Assistant</title>
          <style>
            ** , *::przed, *::po { box-sizing: border-box; }
            body {
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
              max-width: 960px; margin: 2rem auto; padding: 0 1.5rem;
              background: #f6f8fa; color: #1f2328;
            }
            h1 { font-size: 1.6rem; margin-bottom: .25rem; }
            .subtitle { color: #656d76; margin-bottom: 1.5rem; font-size: .95rem; }
            .form-row {
              display: flex; gap: .75rem; flex-wrap: wrap; align-items: flex-end;
              background: #fff; padding: 1rem; border-radius: 8px;
              border: 1px solid #d0d7de; margin-bottom: 1.25rem;
            }
            .form-group { display: flex; flex-direction: column; gap: .3rem; }
            label { font-size: .8rem; font-weight: 600; color: #57606a; text-transform: uppercase; }
            input[type=text], input[type=number] {
              padding: .45rem .65rem; border: 1px solid #d0d7de; border-radius: 6px;
              font-size: .95rem; width: 140px; background: #f6f8fa;
            }
            input[type=number] { width: 90px; }
            button {
              padding: .5rem 1.25rem; background: #0969da; color: #fff;
              border: none; border-radius: 6px; font-size: .95rem;
              cursor: pointer; font-weight: 600; white-space: nowrap;
            }
            button:hover:not(:disabled) { background: #0550ae; }
            button:disabled { opacity: .55; cursor: not-allowed; }
            .status-badge {
              display: inline-block; padding: .25rem .6rem; border-radius: 12px;
              font-size: .8rem; font-weight: 600; background: #eaeef2; color: #57606a;
            }
            .status-badge.streaming { background: #dafbe1; color: #116329; }
            .status-badge.done      { background: #ddf4ff; color: #0550ae; }
            .status-badge.error     { background: #ffebe9; color: #82071e; }
            #output {
              white-space: pre-wrap; word-break: break-word;
              background: #fff; border: 1px solid #d0d7de; border-radius: 8px;
              padding: 1.25rem; min-height: 300px; font-size: .9rem; line-height: 1.6;
              font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace;
            }
            .placeholder { color: #8c959f; font-style: italic; }
            .hint { margin-top: .6rem; font-size: .8rem; color: #8c959f; }
          </style>
        </head>
        <body>
          <h1>🤖 Copilot SDLC — PR Review Assistant</h1>
          <p class="subtitle">
            Demo 18 · GitHub Copilot SDK wywoływany z ASP.NET Core · 3 sesje równoległe (architektura, bezpieczeństwo, wydajność)
          </p>

          <div class="form-row">
            <div class="form-group">
              <label>Owner</label>
              <input id="owner" type="text" value="microsoft" placeholder="microsoft" />
            </div>
            <div class="form-group">
              <label>Repo</label>
              <input id="repo" type="text" value="vscode" placeholder="vscode" />
            </div>
            <div class="form-group">
              <label>PR #</label>
              <input id="pr" type="number" value="321177" min="1" placeholder="12345" />
            </div>
            <div class="form-group">
              <label>API key</label>
              <input id="apiKey" type="password" placeholder="remote only" />
            </div>
            <button id="btn" onclick="startReview()">🔍 Analizuj PR</button>
            <span class="status-badge" id="status">Gotowy</span>
          </div>

          <div id="output"><span class="placeholder">Wyniki analizy pojawią się tutaj podczas streamowania...</span></div>
          <p class="hint">
            Wymaga: <code>GITHUB_TOKEN</code> env var z dostępem do repozytorium ·
            zdalny dostęp wymaga <code>COPILOT_API_KEY</code> ·
            Używa: GitHub MCP Server + GitHub Copilot SDK ·
            Port: 5080
          </p>

          <script>
            let es = null;

            function startReview() {
              const owner  = document.getElementById('owner').value.trim();
              const repo   = document.getElementById('repo').value.trim();
              const pr     = parseInt(document.getElementById('pr').value, 10);
              const apiKey = document.getElementById('apiKey').value.trim();
              const output = document.getElementById('output');
              const btn    = document.getElementById('btn');
              const status = document.getElementById('status');

              if (!owner || !repo || !pr || pr <= 0) {
                alert('Wypełnij wszystkie pola (owner, repo, numer PR).');
                return;
              }

              // Anuluj poprzedni stream
              if (es) { es.close(); es = null; }

              output.innerHTML = '';
              btn.disabled = true;
              status.textContent = '⏳ Łączenie...';
              status.className = 'status-badge';

              let url = `/api/review/stream?owner=${encodeURIComponent(owner)}&repo=${encodeURIComponent(repo)}&pr=${encodeURIComponent(pr)}`;
              if (apiKey) {
                url += `&api_key=${encodeURIComponent(apiKey)}`;
              }
              es = new EventSource(url);

              es.onopen = () => {
                status.textContent = '🔴 Streamowanie...';
                status.className = 'status-badge streaming';
              };

              es.onmessage = (e) => {
                output.textContent += e.data;
                window.scrollTo(0, document.body.scrollHeight);
              };

              es.onerror = (e) => {
                if (es.readyState === EventSource.CLOSED) {
                  // Zamknięte przez serwer (normalne zakończenie) lub błąd
                  const text = output.textContent;
                  if (!text.includes('[DONE]') && !text.includes('SDLC Web Review')) {
                    status.textContent = '❌ Błąd połączenia';
                    status.className = 'status-badge error';
                  }
                  btn.disabled = false;
                  return;
                }
                status.textContent = '⚠️ Błąd — sprawdź konsolę';
                status.className = 'status-badge error';
                es.close();
                btn.disabled = false;
              };
            }

            // Wykryj koniec streamu przez sentinel w tekście
            const observer = new MutationObserver(() => {
              const text = document.getElementById('output').textContent;
              if (text.includes('Demo 18 SDLC Web Review')) {
                const status = document.getElementById('status');
                status.textContent = '✅ Gotowe';
                status.className = 'status-badge done';
                document.getElementById('btn').disabled = false;
                if (es) { es.close(); es = null; }
              }
            });
            observer.observe(document.getElementById('output'), { childList: true, subtree: true, characterData: true });
          </script>
        </body>
        </html>
        """;
}

public static partial class SdlcWebReviewSecurity
{
    public static readonly string[] ReadOnlyGitHubTools =
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
        "web_fetch",
        "glob",
        "github-get_pull_request",
        "github-get_pull_request_files",
        "github-get_pull_request_status",
        "github-get_pull_request_reviews",
        "github-get_pull_request_comments",
        "github-get_pull_request_diff",
        "github-get_issue",
        "github-get_issue_comments",
        "github-list_issues",
        "github-list_pull_requests",
        "github-search_issues",
        "github-search_pull_requests",
        "github-get_file_contents",

    ];

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex RepoRegex();

    public static IResult? ValidateReviewRequest(
        string owner,
        string repo,
        int pr,
        SdlcWebReviewSecurityOptions options)
    {
        if (!IsValidOwner(owner))
            return Results.BadRequest(new { error = "owner must be a valid GitHub account or organization name" });

        if (!IsValidRepository(repo))
            return Results.BadRequest(new { error = "repo must be a valid GitHub repository name" });

        if (pr <= 0)
            return Results.BadRequest(new { error = "pr must be a positive pull request number" });

        if (!options.IsRepositoryAllowed(owner, repo))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return null;
    }

    public static async Task RequireApiKeyAsync(
        HttpContext context,
        RequestDelegate next,
        SdlcWebReviewSecurityOptions options)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        var queryLength = context.Request.QueryString.Value?.Length ?? 0;
        if (queryLength > options.MaxQueryChars)
        {
            context.Response.StatusCode = StatusCodes.Status414UriTooLong;
            await context.Response.WriteAsJsonAsync(new { error = "query string is too large" });
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey) && IsLoopbackRequest(context))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "remote access requires configuring SdlcWebReview:ApiKey or COPILOT_API_KEY"
            });
            return;
        }

        var providedKey = GetProvidedApiKey(context.Request, options.ApiKeyHeaderName);
        if (!ApiKeysMatch(options.ApiKey, providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "valid API key required" });
            return;
        }

        await next(context);
    }

    public static string GetRateLimitPartitionKey(HttpContext context, SdlcWebReviewSecurityOptions options)
    {
        var providedKey = GetProvidedApiKey(context.Request, options.ApiKeyHeaderName);
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && ApiKeysMatch(options.ApiKey, providedKey))
            return $"api-key:{HashForPartition(options.ApiKey)}";

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

#pragma warning disable GHCP001
    public static Task<PermissionDecision> ReadOnlyGitHubMcpPermissionAsync(
        PermissionRequest request,
        PermissionInvocation _)
    {
        // PermissionRequest.Kind może zawierać kwalifikację serwera MCP. Dopasowanie
        // przez ToolMatches pozwala zasadom zatwierdzać narzędzia GitHub tylko do odczytu
        // bez zależności od dokładnego formatu prefiksu z pakietu SDK.
        var kind = request.Kind?.ToString() ?? string.Empty;
        return Task.FromResult(IsAllowedReadOnlyGitHubTool(kind)
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.UserNotAvailable());
    }
#pragma warning restore GHCP001

    public static bool IsAllowedReadOnlyGitHubTool(string toolName) =>
        ToolMatches(toolName, ReadOnlyGitHubTools);

    public static bool IsValidOwner(string owner) =>
        !string.IsNullOrWhiteSpace(owner)
        && OwnerRegex().IsMatch(owner);

    public static bool IsValidRepository(string repo) =>
        !string.IsNullOrWhiteSpace(repo)
        && RepoRegex().IsMatch(repo)
        && !repo.Contains("..", StringComparison.Ordinal)
        && !repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }

    private static string? GetProvidedApiKey(HttpRequest request, string headerName)
    {
        if (request.Headers.TryGetValue(headerName, out var values))
            return values.FirstOrDefault();

        var authorization = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
            return authorization[bearerPrefix.Length..].Trim();

        if (request.Query.TryGetValue("api_key", out var queryValues))
            return queryValues.FirstOrDefault();

        return null;
    }

    private static bool ApiKeysMatch(string expectedKey, string? providedKey)
    {
        if (string.IsNullOrEmpty(providedKey))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expectedKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static bool ToolMatches(string toolName, params string[] names)
    {
        var normalized = toolName.ToLowerInvariant().Replace('\\', '/');
        return names.Any(name =>
            normalized == name ||
            normalized.EndsWith($"/{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($".{name}", StringComparison.Ordinal) ||
            normalized.EndsWith($":{name}", StringComparison.Ordinal));
    }

    private static string HashForPartition(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record SdlcWebReviewSecurityOptions
{
    public const string SectionName = "SdlcWebReview";

    private static readonly string[] DefaultAllowedOrigins =
    [
        "http://localhost:5080",
        "https://localhost:7080",
    ];

    private static readonly string[] DefaultAllowedRepositories =
    [
        "microsoft/vscode",
    ];

    public string[] AllowedOrigins { get; init; } = DefaultAllowedOrigins;
    public string[] AllowedRepositories { get; init; } = DefaultAllowedRepositories;
    public string ApiKey { get; init; } = string.Empty;
    public string ApiKeyHeaderName { get; init; } = "X-API-Key";
    public int MaxQueryChars { get; init; } = 512;
    public int MaxRequestLineBytes { get; init; } = 2 * 1024;
    public int RateLimitPermitLimit { get; init; } = 5;
    public int RateLimitWindowSeconds { get; init; } = 60;

    public static SdlcWebReviewSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var options = new SdlcWebReviewSecurityOptions
        {
            AllowedOrigins = NormalizeOrigins(
                section.GetSection(nameof(AllowedOrigins)).Get<string[]>() ?? DefaultAllowedOrigins),
            AllowedRepositories = NormalizeRepositories(
                GetConfiguredRepositories(section) ?? DefaultAllowedRepositories),
            ApiKey = (Environment.GetEnvironmentVariable("COPILOT_API_KEY")
                ?? section[nameof(ApiKey)]
                ?? string.Empty).Trim(),
            ApiKeyHeaderName = section[nameof(ApiKeyHeaderName)] ?? "X-API-Key",
            MaxQueryChars = GetPositiveInt(section, nameof(MaxQueryChars), 512),
            MaxRequestLineBytes = GetPositiveInt(section, nameof(MaxRequestLineBytes), 2 * 1024),
            RateLimitPermitLimit = GetPositiveInt(section, nameof(RateLimitPermitLimit), 5),
            RateLimitWindowSeconds = GetPositiveInt(section, nameof(RateLimitWindowSeconds), 60),
        };

        options.Validate();
        return options;
    }

    public bool IsRepositoryAllowed(string owner, string repo)
    {
        var repository = $"{owner}/{repo}";
        return AllowedRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase);
    }

    private void Validate()
    {
        if (AllowedOrigins.Length == 0)
            throw new InvalidOperationException("SdlcWebReview:AllowedOrigins must contain at least one origin.");

        if (AllowedRepositories.Length == 0)
            throw new InvalidOperationException("SdlcWebReview:AllowedRepositories must contain at least one owner/repo entry.");

        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName))
            throw new InvalidOperationException("SdlcWebReview:ApiKeyHeaderName is required.");
    }

    private static string[]? GetConfiguredRepositories(IConfiguration section)
    {
        var envValue = Environment.GetEnvironmentVariable("COPILOT_REVIEW_ALLOWED_REPOSITORIES");
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return section.GetSection(nameof(AllowedRepositories)).Get<string[]>();
    }

    private static string[] NormalizeRepositories(IEnumerable<string> repositories)
    {
        return repositories
            .Select(repository => repository.Trim())
            .Where(repository => !string.IsNullOrWhiteSpace(repository))
            .Select(NormalizeRepository)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeRepository(string repository)
    {
        var parts = repository.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !SdlcWebReviewSecurity.IsValidOwner(parts[0]) ||
            !SdlcWebReviewSecurity.IsValidRepository(parts[1]))
            throw new InvalidOperationException($"Invalid repository allowlist entry '{repository}'. Expected owner/repo.");

        return $"{parts[0]}/{parts[1]}";
    }

    private static string[] NormalizeOrigins(IEnumerable<string> origins)
    {
        return origins
            .Select(origin => origin.Trim())
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeOrigin(string origin)
    {
        if (origin.Contains('*', StringComparison.Ordinal))
            throw new InvalidOperationException("Wildcard CORS origins are not allowed for SdlcWebReview.");

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException($"Invalid CORS origin '{origin}'.");

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static int GetPositiveInt(IConfiguration section, string key, int defaultValue)
    {
        var configured = section.GetValue<int?>(key);
        if (configured is null)
            return defaultValue;

        if (configured <= 0)
            throw new InvalidOperationException($"SdlcWebReview:{key} must be greater than zero.");

        return configured.Value;
    }
}
