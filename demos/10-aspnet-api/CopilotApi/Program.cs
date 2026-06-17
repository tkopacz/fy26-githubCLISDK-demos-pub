// Demo 10 — CopilotApi: API objaśniającego kod na żywo
//
// Pokazuje: GitHub Copilot SDK jako singleton serwis w ASP.NET Core + SSE streaming.
// Endpointy:
//   POST /api/explain       — wyjaśnienie kodu (czeka na całość, zwraca JSON)
//   POST /api/explain/stream — SSE streaming tokenów na żywo
//   POST /api/review        — structured code review (JSON)
//   GET  /health            — stan połączenia Copilot
//
// Uruchomienie: dotnet run --project demos/10-aspnet-api/CopilotApi
// Test SSE: curl -N -X POST "http://localhost:5078/api/explain/stream" -H "Content-Type: application/json" -d "{\"prompt\":\"explain async await\",\"language\":\"csharp\"}"

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotSDK.Demos.Shared.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using ProviderConfig = GitHub.Copilot.ProviderConfig;

var builder = WebApplication.CreateBuilder(args);
var securityOptions = CopilotApiSecurityOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(securityOptions);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes;
});

// === Singleton CopilotClient ===
// CopilotClient jest właścicielem połączenia/procesu środowiska wykonawczego SDK. W aplikacji
// webowej powinien być jedną, długotrwałą usługą współdzieloną przez cały host, a nie jednym
// klientem na żądanie HTTP. Poszczególne żądania tworzą z niego krótkotrwałe instancje CopilotSession.
//
// Rejestrujemy tę samą instancję CopilotService zarówno jako singleton, jak i usługę hostowaną.
// Wywołanie AddHostedService<CopilotService>() bezpośrednio utworzyłoby drugą
// instancję, co oznaczałoby drugi cykl życia CopilotClient/runtime.
builder.Services.AddSingleton<CopilotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotService>());

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(securityOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            CopilotApiSecurity.GetRateLimitPartitionKey(ctx, securityOptions),
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
app.Use((ctx, next) => CopilotApiSecurity.RequireApiKeyAsync(ctx, next, securityOptions));

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

// ── POST /api/explain ─────────────────────────────────────────────────────────
app.MapPost("/api/explain", async (
    ExplainRequest request,
    CopilotService copilot,
    CopilotApiSecurityOptions security,
    CancellationToken ct) =>
{
    if (CopilotApiSecurity.ValidateRequiredText(request.Prompt, "prompt", security.MaxPromptChars) is { } validationError)
        return validationError;

    // ExplainAsync tworzy nową sesję SDK dla tego żądania i czeka na
    // ostatnią wiadomość asystenta przed zwróceniem JSON do wywołującego.
    var result = await copilot.ExplainAsync(request.Prompt, request.Language, ct);
    return Results.Json(new { explanation = result, prompt = request.Prompt });
});

// ── POST /api/explain/stream ──────────────────────────────────────────────────
app.MapPost("/api/explain/stream", async (
    ExplainRequest request,
    CopilotService copilot,
    CopilotApiSecurityOptions security,
    HttpContext ctx,
    CancellationToken ct) =>
{
    if (CopilotApiSecurity.ValidateRequiredText(request.Prompt, "prompt", security.MaxPromptChars) is { } validationError)
    {
        await validationError.ExecuteAsync(ctx);
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    // ExplainStreamAsync konwertuje strumień AssistantMessageDeltaEvent z SDK
    // na asynchroniczny strumień (IAsyncEnumerable) ASP.NET, a ten punkt końcowy opakowuje każdą porcję
    // jako zdarzenie Server-Sent Event.
    await foreach (var token in copilot.ExplainStreamAsync(request.Prompt, request.Language, ct))
    {
        var data = $"data: {JsonSerializer.Serialize(token)}\n\n";
        await ctx.Response.WriteAsync(data, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
});

// ── POST /api/review ──────────────────────────────────────────────────────────
app.MapPost("/api/review", async (
    ReviewRequest request,
    CopilotService copilot,
    CopilotApiSecurityOptions security,
    CancellationToken ct) =>
{
    if (CopilotApiSecurity.ValidateRequiredText(request.Code, "code", security.MaxCodeChars) is { } validationError)
        return validationError;

    // ReviewAsync używa sesji SDK bez strumieniowania z komunikatem systemowym wymuszającym format JSON,
    // a następnie deserializuje końcową odpowiedź asystenta do DTO API.
    var review = await copilot.ReviewAsync(request.Code, request.Language ?? "csharp", ct);
    return Results.Json(review);
});

app.Run();

// ── CopilotService ────────────────────────────────────────────────────────────

public class CopilotService : IHostedService, IAsyncDisposable
{
    private CopilotClient? _client;
    private readonly string _model;
    private readonly ProviderConfig? _provider;
    private readonly CopilotApiSecurityOptions _securityOptions;
    private volatile bool _isConnected;
    private DateTimeOffset _lastHealthCheckUtc = DateTimeOffset.UtcNow;

    public bool IsConnected => _client is not null && _isConnected;

    public CopilotService(CopilotApiSecurityOptions securityOptions)
    {
        _securityOptions = securityOptions;
        _model = CopilotClientFactory.GetModelId();
        _provider = CopilotClientFactory.GetByokProvider();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Opcje klienta bezpieczne dla serwera izolują stan środowiska wykonawczego SDK oraz katalog
        // roboczy od katalogu procesu ASP.NET. Ma to znaczenie, ponieważ
        // hostowane interfejsy API nie powinny pośrednio udostępniać narzędziom katalogu głównego repozytorium,
        // hooków plikowych ani wykrywania konfiguracji.
        _client = CopilotClientFactory.CreateServerSafe(
            _securityOptions.ClientBaseDirectory,
            _securityOptions.SessionWorkingDirectory);
        await CheckHealthAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
    }

    public async Task<CopilotHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        if (_client is null)
            return SetHealth(false, "Disconnected", "CopilotClient not started.");

        try
        {
            // Gotowość uwierzytelniania/modelu sprawdzamy na kliencie, ponieważ weryfikuje ona
            // połączenie środowiska wykonawczego, a nie konkretną rozmowę.
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

    public async Task<string> ExplainAsync(
        string prompt,
        string? language = null,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("CopilotClient not started.");

        // Tworzymy jedną CopilotSession na żądanie. Sesje przechowują historię
        // rozmowy, więc współdzielenie sesji między wywołującymi HTTP spowodowałoby wyciek kontekstu
        // i tworzyłoby sytuacje wyścigu.
        await using var session = await _client.CreateSessionAsync(
            CreateSafeSessionConfig(BuildExplainerSystemMessage(language)),
            ct);

        // Pomocnik wysyła prompt i czeka na SessionIdleEvent, udostępniając
        // API w konwencjonalnej postaci Task<string> ponad strumieniem zdarzeń SDK.
        return await SessionHelper.SendAndWaitAsync(session, prompt, ct);
    }

    public async IAsyncEnumerable<string> ExplainStreamAsync(
        string prompt,
        string? language = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("CopilotClient not started.");

        // Strumieniowanie jest włączane per-sesja, więc to żądanie otrzymuje
        // porcje AssistantMessageDeltaEvent. Inne punkty końcowe mogą nadal korzystać
        // z sesji bez strumieniowania na tym samym kliencie.
        await using var session = await _client.CreateSessionAsync(
            CreateSafeSessionConfig(BuildExplainerSystemMessage(language), streaming: true),
            ct);

        // SessionChannelBridge adaptuje callbacki session.On do IAsyncEnumerable,
        // co jest naturalną postacią strumieniowania odpowiedzi w ASP.NET.
        using var bridge = new SessionChannelBridge();
        bridge.Attach(session);

        // Podłączenie mostu przed SendAsync gwarantuje, że wczesne delty/błędy
        // zostaną przechwycone. SendAsync rozpoczyna turę; most zamyka się, gdy SDK
        // wyemituje SessionIdleEvent lub SessionErrorEvent.
        await session.SendAsync(new MessageOptions { Prompt = prompt }, ct);

        await foreach (var token in bridge.ReadAllAsync(ct))
        {
            yield return token;
        }
    }

    public async Task<CodeReview> ReviewAsync(
        string code,
        string language,
        CancellationToken ct = default)
    {
        if (_client is null) throw new InvalidOperationException("CopilotClient not started.");

        // Przegląd kodu otrzymuje własną, krótkotrwałą sesję i komunikat systemowy, dzięki czemu
        // nie dziedziczy historii promptów z wywołań /api/explain.
        await using var session = await _client.CreateSessionAsync(
            CreateSafeSessionConfig("""
                You are a senior code reviewer. Analyze the provided code and respond with ONLY a JSON object
                (no markdown, no explanation) with this exact structure:
                {
                  "summary": "one sentence summary",
                  "score": 1-10,
                  "issues": [{"severity": "HIGH|MEDIUM|LOW", "category": "...", "description": "...", "line": N}],
                  "strengths": ["...", "..."],
                  "recommendations": ["...", "..."]
                }
                """),
            ct);

        var raw = await SessionHelper.SendAndWaitAsync(session,
            $"Review this {language} code:\n\n```{language}\n{code}\n```",
            ct);

        var json = ExtractJson(raw);

        try
        {
            return JsonSerializer.Deserialize<CodeReview>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new CodeReview("Could not parse review", 0, [], [], []);
        }
        catch
        {
            return new CodeReview(raw, 0, [], [], []);
        }
    }

    private SessionConfig CreateSafeSessionConfig(string systemMessage, bool streaming = false)
    {
        return new SessionConfig
        {
            // ClientName oznacza ten host w metadanych SDK/runtime i jest przydatny
            // przy diagnozowaniu zdarzeń pochodzących z kilku aplikacji osadzających SDK.
            ClientName = "CopilotApiDemo10",
            Model = _model,
            Provider = _provider,
            // Ograniczamy katalog roboczy sesji SDK do skonfigurowanego
            // sandboxa. Mimo że ten interfejs API wyłącza narzędzia, bezpieczne wartości domyślne mają
            // znaczenie, gdy przyszłe dema dodadzą dostęp do narzędzi.
            WorkingDirectory = _securityOptions.SessionWorkingDirectory,
            // Pusty zestaw narzędzi i uprawnienia „odmów wszystko” sprawiają, że hostowany interfejs API
            // jedynie prosi model o wygenerowanie żądanych danych wyjściowych; nie może on wywoływać
            // lokalnych narzędzi ani wykonywać operacji na systemie plików/powłoce.
            AvailableTools = new ToolSet(),
            OnPermissionRequest = DenyPermissionAsync,
            // Wyłączamy funkcje automatycznego wykrywania, aby publiczne żądanie HTTP nie mogło
            // przypadkowo przejąć konfiguracji repozytorium, niestandardowych instrukcji,
            // lokalnych skilli, operacji Git, harmonogramów ani danych z trwałego magazynu
            // sesji w środowisku serwerowym.
            EnableConfigDiscovery = false,
            EnableFileHooks = false,
            EnableHostGitOperations = false,
            EnableSessionStore = false,
            EnableSkills = false,
            SkipEmbeddingRetrieval = true,
            EmbeddingCacheStorage = EmbeddingCacheStorageMode.InMemory,
            EnableOnDemandInstructionDiscovery = false,
            SkipCustomInstructions = true,
            CustomAgentsLocalOnly = true,
            CoauthorEnabled = false,
            ManageScheduleEnabled = false,
            EnableSessionTelemetry = false,
            // Streaming kontroluje, czy SDK emituje przyrostowe
            // fragmenty AssistantMessageDeltaEvent dla tej sesji.
            Streaming = streaming,
            SystemMessage = new SystemMessageConfig
            {
                // Tryb zamiany (Replace) daje API pełną kontrolę nad zachowaniem
                // sesji dla tego żądania, zamiast dołączać treść do wartości domyślnych.
                Mode = SystemMessageMode.Replace,
                Content = systemMessage,
            },
        };
    }

#pragma warning disable GHCP001
    private static Task<PermissionDecision> DenyPermissionAsync(
        PermissionRequest request,
        PermissionInvocation invocation)
    {
        // W interfejsie API bezpieczną wartością domyślną jest odrzucenie każdego żądania uprawnień
        // od SDK, chyba że konkretny punkt końcowy został zaprojektowany tak, by je dopuszczać.
        return Task.FromResult(PermissionDecision.UserNotAvailable());
    }
#pragma warning restore GHCP001

    private static string BuildExplainerSystemMessage(string? language) =>
        $"""
        You are a helpful code explainer for {language ?? "any programming language"}.
        Explain code clearly and concisely. Use examples where helpful.
        For technical concepts, relate them to practical real-world use cases.
        Keep explanations focused and avoid unnecessary verbosity.
        """;

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

record ExplainRequest(string Prompt, string? Language);

record ReviewRequest(string Code, string? Language);

public record CodeReview(
    string Summary,
    int Score,
    List<ReviewIssue> Issues,
    List<string> Strengths,
    List<string> Recommendations);

public record ReviewIssue(string Severity, string Category, string Description, int? Line);

public sealed record CopilotHealth(
    bool IsHealthy,
    string State,
    string? Reason,
    DateTimeOffset CheckedAtUtc);

public static class CopilotApiSecurity
{
    public static IResult? ValidateRequiredText(string? value, string fieldName, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Results.BadRequest(new { error = $"{fieldName} is required" });

        if (value.Length > maxChars)
            return Results.BadRequest(new { error = $"{fieldName} exceeds {maxChars} characters" });

        return null;
    }

    public static async Task RequireApiKeyAsync(
        HttpContext context,
        RequestDelegate next,
        CopilotApiSecurityOptions options)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
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
                error = "remote access requires configuring CopilotApi:ApiKey or COPILOT_API_KEY"
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

    public static string GetRateLimitPartitionKey(HttpContext context, CopilotApiSecurityOptions options)
    {
        var providedKey = GetProvidedApiKey(context.Request, options.ApiKeyHeaderName);
        if (!string.IsNullOrWhiteSpace(options.ApiKey) && ApiKeysMatch(options.ApiKey, providedKey))
            return $"api-key:{HashForPartition(options.ApiKey)}";

        return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

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

    private static string HashForPartition(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record CopilotApiSecurityOptions
{
    public const string SectionName = "CopilotApi";

    private static readonly string[] DefaultAllowedOrigins =
    [
        "http://localhost:5078",
        "https://localhost:7299",
    ];

    public string[] AllowedOrigins { get; init; } = DefaultAllowedOrigins;
    public string ApiKey { get; init; } = string.Empty;
    public string ApiKeyHeaderName { get; init; } = "X-API-Key";
    public int MaxRequestBodyBytes { get; init; } = 64 * 1024;
    public int MaxPromptChars { get; init; } = 12_000;
    public int MaxCodeChars { get; init; } = 50_000;
    public int RateLimitPermitLimit { get; init; } = 20;
    public int RateLimitWindowSeconds { get; init; } = 60;
    public string ClientBaseDirectory { get; init; } =
        Path.Combine(DefaultApplicationDataDirectory, "copilot-home");
    public string SessionWorkingDirectory { get; init; } =
        Path.Combine(DefaultApplicationDataDirectory, "sandbox");

    public static CopilotApiSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var options = new CopilotApiSecurityOptions
        {
            AllowedOrigins = NormalizeOrigins(
                section.GetSection(nameof(AllowedOrigins)).Get<string[]>() ?? DefaultAllowedOrigins),
            ApiKey = (Environment.GetEnvironmentVariable("COPILOT_API_KEY")
                ?? section[nameof(ApiKey)]
                ?? string.Empty).Trim(),
            ApiKeyHeaderName = section[nameof(ApiKeyHeaderName)] ?? "X-API-Key",
            MaxRequestBodyBytes = GetPositiveInt(section, nameof(MaxRequestBodyBytes), 64 * 1024),
            MaxPromptChars = GetPositiveInt(section, nameof(MaxPromptChars), 12_000),
            MaxCodeChars = GetPositiveInt(section, nameof(MaxCodeChars), 50_000),
            RateLimitPermitLimit = GetPositiveInt(section, nameof(RateLimitPermitLimit), 20),
            RateLimitWindowSeconds = GetPositiveInt(section, nameof(RateLimitWindowSeconds), 60),
            ClientBaseDirectory = section[nameof(ClientBaseDirectory)]
                ?? Path.Combine(DefaultApplicationDataDirectory, "copilot-home"),
            SessionWorkingDirectory = section[nameof(SessionWorkingDirectory)]
                ?? Path.Combine(DefaultApplicationDataDirectory, "sandbox"),
        };

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (AllowedOrigins.Length == 0)
            throw new InvalidOperationException("CopilotApi:AllowedOrigins must contain at least one origin.");

        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName))
            throw new InvalidOperationException("CopilotApi:ApiKeyHeaderName is required.");

        if (string.IsNullOrWhiteSpace(ClientBaseDirectory))
            throw new InvalidOperationException("CopilotApi:ClientBaseDirectory is required.");

        if (string.IsNullOrWhiteSpace(SessionWorkingDirectory))
            throw new InvalidOperationException("CopilotApi:SessionWorkingDirectory is required.");
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
            throw new InvalidOperationException("Wildcard CORS origins are not allowed for CopilotApi.");

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
            throw new InvalidOperationException($"CopilotApi:{key} must be greater than zero.");

        return configured.Value;
    }

    private static string DefaultApplicationDataDirectory
    {
        get
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var root = string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.GetTempPath()
                : localApplicationData;

            return Path.Combine(root, "CopilotSDK.Demos", "CopilotApi");
        }
    }
}
