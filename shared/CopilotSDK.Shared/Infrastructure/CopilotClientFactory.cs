using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;

namespace CopilotSDK.Demos.Shared.Infrastructure;

/// <summary>
/// Centralna fabryka instancji <see cref="CopilotClient"/> używanych w projektach demonstracyjnych.
///
/// W GitHub Copilot SDK <see cref="CopilotClient"/> jest długowiecznym obiektem
/// będącym właścicielem połączenia ze środowiskiem wykonawczym Copilot. To środowisko
/// to ten sam silnik agenta, który obsługuje Copilot CLI, udostępniony dla .NET przez JSON-RPC.
/// Projekty demonstracyjne korzystają z tej fabryki zamiast tworzyć klienta bezpośrednio,
/// dzięki czemu każdy z nich ma ten sam wybór modelu, konfigurację dostawcy BYOK, telemetrię
/// oraz domyślne ustawienia sesji zdalnych.
/// </summary>
public static class CopilotClientFactory
{
    private const string DefaultModelId = "gpt-5.4-mini";
    private const string ByokDefaultModelId = "gpt-4o";
    private const string RemoteRuntimeHandshakeFilePrefix = "copilot-remote-runtime-handshake";
    private const string RemoteSessionStateFilePrefix = "copilot-remote-session-state";
    private static readonly TimeSpan RemoteStateTtl = TimeSpan.FromMinutes(30);
    private static readonly string RuntimeStateDirectoryPath =
        Path.Combine(GetApplicationDataRoot(), "runtime-state");
    private static readonly string TelemetryFilePathValue =
        Path.Combine(RuntimeStateDirectoryPath, $"copilot-traces-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl");

    public static string GetModelId(string? modelId = null)
    {
        // COPILOT_MODEL to nadpisanie kierowane do SDK: każdy SessionConfig.Model
        // w projektach demonstracyjnych ostatecznie trafia tutaj, więc prezenterzy mogą
        // zmienić model bez modyfikowania kodu źródłowego.
        var copilotModel = Environment.GetEnvironmentVariable("COPILOT_MODEL")?.Trim();
        if (!string.IsNullOrWhiteSpace(copilotModel))
            return copilotModel;

        // Dostawcy BYOK używają natywnych identyfikatorów modeli dostawcy, więc ta wartość
        // domyślna różni się od modelu hostowanego przez GitHub Copilot używanego w trybie normalnym.
        if (string.Equals(Environment.GetEnvironmentVariable("BYOK_MODE"), "1", StringComparison.Ordinal))
            return ByokDefaultModelId;

        return string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
    }

    public static string RuntimeStateDirectory => EnsureDirectory(RuntimeStateDirectoryPath);

    public static string RemoteRuntimeHandshakeFilePath =>
        GetActiveStateFilePath(RemoteRuntimeHandshakePointerFilePath) ?? RemoteRuntimeHandshakePointerFilePath;

    public static string RemoteSessionStateFilePath =>
        GetActiveStateFilePath(RemoteSessionStatePointerFilePath) ?? RemoteSessionStatePointerFilePath;

    private static string RemoteRuntimeHandshakePointerFilePath =>
        Path.Combine(RuntimeStateDirectory, $"{RemoteRuntimeHandshakeFilePrefix}.current");

    private static string RemoteSessionStatePointerFilePath =>
        Path.Combine(RuntimeStateDirectory, $"{RemoteSessionStateFilePrefix}.current");

    private static string LegacyRemoteRuntimeHandshakeFilePath =>
        Path.Combine(RuntimeStateDirectory, $"{RemoteRuntimeHandshakeFilePrefix}.json");

    private static string LegacyRemoteSessionStateFilePath =>
        Path.Combine(RuntimeStateDirectory, $"{RemoteSessionStateFilePrefix}.json");

    /// <summary>
    /// Ścieżka pliku JSONL z telemetrią emitowaną przez Copilot CLI.
    /// </summary>
    public static string TelemetryFilePath
    {
        get
        {
            _ = RuntimeStateDirectory;
            return TelemetryFilePathValue;
        }
    }

    public static CopilotClient Create(
        bool enableTelemetryFile = false,
        bool captureTelemetryContent = false)
    {
        // CopilotClientOptions steruje procesem/połączeniem środowiska wykonawczego SDK.
        // W projektach demonstracyjnych ścieżka tworzenia klienta jest celowo wąska, ponieważ
        // klient ma być pojedynczy na aplikację; poszczególne tury korzystają z lekkich
        // instancji CopilotSession utworzonych na podstawie tego klienta.
        var options = CreateOptions(enableTelemetryFile, captureTelemetryContent);

        return new CopilotClient(options);
    }

    public static CopilotClient CreateServerSafe(
        string baseDirectory,
        string workingDirectory,
        bool enableTelemetryFile = false,
        bool captureTelemetryContent = false)
    {
        // Wersje demonstracyjne ASP.NET i serwerów zdalnych nie powinny dziedziczyć dowolnego
        // katalogu roboczego powłoki. Te opcje izolują stan środowiska wykonawczego SDK oraz
        // wykonywanie narzędzi do jawnych folderów będących własnością procesu hosta.
        var options = CreateServerSafeOptions(baseDirectory, workingDirectory, enableTelemetryFile, captureTelemetryContent);

        Directory.CreateDirectory(options.BaseDirectory!);
        Directory.CreateDirectory(options.WorkingDirectory!);

        return new CopilotClient(options);
    }

    internal static CopilotClientOptions CreateServerSafeOptions(
        string baseDirectory,
        string workingDirectory,
        bool enableTelemetryFile = false,
        bool captureTelemetryContent = false)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Base directory is required.", nameof(baseDirectory));

        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Working directory is required.", nameof(workingDirectory));

        var options = CreateOptions(enableTelemetryFile, captureTelemetryContent);
        // Tryb Empty uruchamia środowisko wykonawcze SDK bez ładowania kontekstu lokalnego
        // repozytorium. Jest to bezpieczniejsze dla wersji demonstracyjnych w stylu serwerowym, ponieważ każde
        // żądanie decyduje, jaki kontekst, narzędzia i uprawnienia powinna otrzymać sesja.
        options.Mode = CopilotClientMode.Empty;
        options.BaseDirectory = Path.GetFullPath(baseDirectory);
        options.WorkingDirectory = Path.GetFullPath(workingDirectory);
        // Sesje zdalne to osobna funkcja SDK demonstrowana jawnie w wersji demonstracyjnej 17.
        // Bezpieczni dla serwera klienci lokalni wyłączają ją, aby nie reklamować
        // niepotrzebnej powierzchni do zdalnego podłączania.
        options.EnableRemoteSessions = false;
        // SessionIdleEvent to sygnał SDK o zakończeniu tury. Utrzymywanie limitu czasu
        // bezczynności na środowiskach wykonawczych hostowanych na serwerze chroni przed
        // bezterminowym wstrzymywaniem zasobów przez porzucone sesje, gdy klient się rozłączy.
        options.SessionIdleTimeoutSeconds = 300;

        return options;
    }

    private static CopilotClientOptions CreateOptions(bool enableTelemetryFile, bool captureTelemetryContent)
    {
        var options = new CopilotClientOptions();

        if (enableTelemetryFile)
        {
            // TelemetryConfig prosi środowisko wykonawcze Copilot o wyeksportowanie zakresów
            // SDK/środowiska wykonawczego do pliku JSONL. CaptureContent jest celowo opcją
            // typu opt-in, ponieważ monity i odpowiedzi modelu mogą zawierać poufne treści.
            options.Telemetry = new TelemetryConfig
            {
                FilePath = TelemetryFilePath,
                ExporterType = "file",
                SourceName = "CopilotSDK.Demos",
                CaptureContent = captureTelemetryContent,
            };
        }

        return options;
    }

    public static CopilotClient CreateRemoteServer(int port = 0, string? connectionToken = null)
    {
        // RuntimeConnection.ForTcp zamienia ten proces w serwer środowiska wykonawczego
        // GitHub Copilot SDK. Inny proces może połączyć się z tym samym środowiskiem wykonawczym
        // i wznowić pracę bez uruchamiania własnego lokalnego procesu Copilot CLI.
        var token = string.IsNullOrWhiteSpace(connectionToken)
            ? Guid.NewGuid().ToString("N")
            : connectionToken;

        return new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForTcp(port, token, path: null, args: null),
        });
    }

    public static CopilotClient CreateRemoteClient(string runtimeUrl, string? connectionToken = null)
    {
        // RuntimeConnection.ForUri to strona klienta sesji zdalnych: SDK łączy się
        // z już działającym środowiskiem wykonawczym zamiast tworzyć nowe.
        return new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForUri(runtimeUrl, connectionToken),
        });
    }

    public static async Task<string> SaveRemoteRuntimeHandshakeAsync(RemoteRuntimeHandshake handshake)
    {
        // Wersja demonstracyjna 17 używa tego uchwytu (handshake) jako lekkiego dokumentu
        // odnajdywania zdalnego środowiska wykonawczego SDK. Zawiera on metadane połączenia,
        // a nie historię czatu; historia sesji pozostaje własnością środowiska wykonawczego Copilot.
        var state = handshake with { ExpiresAtUtc = ResolveExpiry(handshake.ExpiresAtUtc) };
        var path = CreateRemoteStateFilePath(RemoteRuntimeHandshakeFilePrefix);
        var json = JsonSerializer.Serialize(state, JsonOptions);

        await WriteProtectedStateFileAsync(path, json);
        await WriteStatePointerAsync(RemoteRuntimeHandshakePointerFilePath, path, state.ExpiresAtUtc);
        DeleteFileIfExists(LegacyRemoteRuntimeHandshakeFilePath);

        return path;
    }

    public static async Task<RemoteRuntimeHandshake?> LoadRemoteRuntimeHandshakeAsync()
    {
        // Zwrócenie null dla brakujących/wygasłych uchwytów pozwala wywołującym wycofać się
        // do uruchomienia lokalnego środowiska wykonawczego zamiast łączyć się z nieaktualnym
        // punktem końcowym SDK.
        var path = GetActiveStateFilePath(RemoteRuntimeHandshakePointerFilePath) ?? LegacyRemoteRuntimeHandshakeFilePath;
        if (!File.Exists(path))
            return null;

        var json = await ReadProtectedStateFileAsync(path);
        var state = JsonSerializer.Deserialize<RemoteRuntimeHandshake>(json, JsonOptions);
        if (state is null || IsExpired(state.ExpiresAtUtc))
        {
            DeleteRemoteRuntimeHandshake();
            return null;
        }

        return state;
    }

    public static async Task<string> SaveRemoteSessionStateAsync(RemoteSessionState state)
    {
        // Identyfikatory sesji można bezpiecznie zachować w wersji demonstracyjnej, ale token
        // połączenia i oryginalny monit są celowo usuwane przed zapisaniem stanu na dysk.
        // Token uwierzytelnia w zdalnym środowisku wykonawczym SDK, a monit może zawierać dane użytkownika.
        var sanitizedState = state with
        {
            ExpiresAtUtc = ResolveExpiry(state.ExpiresAtUtc),
            ConnectionToken = null,
            Prompt = null,
        };
        var path = CreateRemoteStateFilePath(RemoteSessionStateFilePrefix);
        var json = JsonSerializer.Serialize(sanitizedState, JsonOptions);

        await WriteProtectedStateFileAsync(path, json);
        await WriteStatePointerAsync(RemoteSessionStatePointerFilePath, path, sanitizedState.ExpiresAtUtc);
        DeleteFileIfExists(LegacyRemoteSessionStateFilePath);

        return path;
    }

    public static async Task<RemoteSessionState?> LoadRemoteSessionStateAsync()
    {
        // Wznowiony klient przekazuje ten identyfikator sesji z powrotem do SDK, aby środowisko
        // wykonawcze mogło dołączyć do istniejącego stanu rozmowy zamiast tworzyć
        // zupełnie nową sesję CopilotSession.
        var path = GetActiveStateFilePath(RemoteSessionStatePointerFilePath) ?? LegacyRemoteSessionStateFilePath;
        if (!File.Exists(path))
            return null;

        var json = await ReadProtectedStateFileAsync(path);
        var state = JsonSerializer.Deserialize<RemoteSessionState>(json, JsonOptions);
        if (state is null || IsExpired(state.ExpiresAtUtc))
        {
            DeleteRemoteSessionState();
            return null;
        }

        return state;
    }

    public static void DeleteRemoteRuntimeHandshake() =>
        DeleteRemoteState(RemoteRuntimeHandshakePointerFilePath, LegacyRemoteRuntimeHandshakeFilePath);

    public static void DeleteRemoteSessionState() =>
        DeleteRemoteState(RemoteSessionStatePointerFilePath, LegacyRemoteSessionStateFilePath);

    public static void DeleteTelemetryFile() =>
        DeleteFileIfExists(TelemetryFilePath);

    public static string CreateSecretFingerprint(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static async Task WriteProtectedStateFileAsync(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            EnsureDirectory(directory);

        var protectedJson = ProtectStateJson(json);
        await File.WriteAllTextAsync(path, protectedJson);
        RestrictFilePermissions(path);
    }

    private static async Task<string> ReadProtectedStateFileAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var envelope = TryDeserializeProtectedStateEnvelope(json);
        if (envelope is null)
            return json;

        var payload = Convert.FromBase64String(envelope.Payload);
        return envelope.Protection switch
        {
            "dpapi-current-user" when OperatingSystem.IsWindows() => Encoding.UTF8.GetString(UnprotectForCurrentUser(payload)),
            "dpapi-current-user" => throw new InvalidOperationException("DPAPI-protected remote state can only be read on Windows by the current user."),
            "file-permissions" => Encoding.UTF8.GetString(payload),
            _ => throw new InvalidOperationException($"Unsupported remote state protection '{envelope.Protection}'."),
        };
    }

    private static async Task WriteStatePointerAsync(string pointerPath, string stateFilePath, DateTimeOffset expiresAtUtc)
    {
        var pointer = new RemoteStatePointer(stateFilePath, expiresAtUtc);
        await File.WriteAllTextAsync(pointerPath, JsonSerializer.Serialize(pointer, JsonOptions));
        RestrictFilePermissions(pointerPath);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteRemoteState(string pointerPath, string legacyPath)
    {
        var statePath = GetActiveStateFilePath(pointerPath);
        if (!string.IsNullOrWhiteSpace(statePath))
            DeleteFileIfExists(statePath);

        DeleteFileIfExists(pointerPath);
        DeleteFileIfExists(legacyPath);
    }

    private static string CreateRemoteStateFilePath(string prefix)
    {
        var fileName = $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.json";
        return Path.Combine(RuntimeStateDirectory, fileName);
    }

    private static string? GetActiveStateFilePath(string pointerPath)
    {
        if (!File.Exists(pointerPath))
            return null;

        RemoteStatePointer? pointer;
        try
        {
            pointer = JsonSerializer.Deserialize<RemoteStatePointer>(File.ReadAllText(pointerPath), JsonOptions);
        }
        catch (JsonException)
        {
            DeleteFileIfExists(pointerPath);
            return null;
        }

        if (pointer is null || IsExpired(pointer.ExpiresAtUtc) || !File.Exists(pointer.Path))
        {
            if (pointer is not null && IsExpired(pointer.ExpiresAtUtc))
                DeleteFileIfExists(pointer.Path);

            DeleteFileIfExists(pointerPath);
            return null;
        }

        return pointer.Path;
    }

    private static DateTimeOffset ResolveExpiry(DateTimeOffset expiresAtUtc) =>
        expiresAtUtc == default ? DateTimeOffset.UtcNow.Add(RemoteStateTtl) : expiresAtUtc;

    private static bool IsExpired(DateTimeOffset expiresAtUtc) =>
        expiresAtUtc != default && expiresAtUtc <= DateTimeOffset.UtcNow;

    private static ProtectedStateEnvelope? TryDeserializeProtectedStateEnvelope(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ProtectedStateEnvelope>(json, JsonOptions);
            return string.IsNullOrWhiteSpace(envelope?.Protection) || string.IsNullOrWhiteSpace(envelope.Payload)
                ? null
                : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ProtectStateJson(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var envelope = OperatingSystem.IsWindows()
            ? new ProtectedStateEnvelope("dpapi-current-user", Convert.ToBase64String(ProtectForCurrentUser(payload)))
            : new ProtectedStateEnvelope("file-permissions", Convert.ToBase64String(payload));

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectForCurrentUser(byte[] payload) =>
        ProtectedData.Protect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectForCurrentUser(byte[] payload) =>
        ProtectedData.Unprotect(payload, optionalEntropy: null, DataProtectionScope.CurrentUser);

    private static string EnsureDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        RestrictDirectoryPermissions(directory.FullName);
        return directory.FullName;
    }

    private static string GetApplicationDataRoot()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localApplicationData)
            ? Path.GetTempPath()
            : localApplicationData;

        return Path.Combine(root, "CopilotSDK.Demos");
    }

    private static void RestrictDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static async Task GetModelsListForCopilot(CopilotClient client)
    {
        // Uwierzytelnianie i odnajdywanie modeli to wywołania SDK na poziomie klienta. Nie wymagają
        // one sesji CopilotSession, ponieważ odpytują możliwości konta/środowiska wykonawczego,
        // a nie uczestniczą w rozmowie.
        var auth = await client.GetAuthStatusAsync();
        if (!auth.IsAuthenticated)
        {
            Console.WriteLine("Nie jesteś zalogowany do Copilota.");
            return;
        }

        var models = await client.ListModelsAsync();
        Console.WriteLine("Dostępne modele:");
        foreach (var model in models)
        {
            Console.WriteLine($"Id: {model.Id}, Name: {model.Name}, (MaxContextWindowTokens: {model.Capabilities.Limits.MaxContextWindowTokens})");
        }
    }


    /// <summary>
    /// Buduje konfigurację dostawcy BYOK, gdy ustawiono BYOK_MODE=1.
    ///
    /// ProviderConfig jest przekazywany do SessionConfig.Provider, a nie do opcji
    /// klienta Copilot. To rozróżnienie ma znaczenie w SDK: klient jest właścicielem
    /// połączenia wykonawczego, podczas gdy każda sesja wybiera model i dostawcę,
    /// którego agent powinien użyć do tej rozmowy.
    /// </summary>
    public static ProviderConfig? GetByokProvider()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BYOK_MODE"), "1", StringComparison.Ordinal))
            return null;

        var providerName = (Environment.GetEnvironmentVariable("BYOK_PROVIDER") ?? "openai").Trim().ToLowerInvariant();
        if (!new[] { "openai", "anthropic", "azure" }.Contains(providerName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported BYOK provider '{providerName}'. Supported values: openai, anthropic, azure.");

        var apiKey = Environment.GetEnvironmentVariable("BYOK_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("BYOK_MODE=1 requires BYOK_API_KEY to be set.");

        var baseUrl = (Environment.GetEnvironmentVariable("BYOK_BASE_URL") ?? string.Empty).Trim();
        if (string.Equals(providerName, "azure", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                "BYOK_PROVIDER=azure requires BYOK_BASE_URL, for example " +
                "https://<resource>.openai.azure.com/openai/deployments/<deployment>.");

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = providerName switch
            {
                "openai" => "https://api.openai.com/v1",
                "anthropic" => "https://api.anthropic.com/v1",
                _ => throw new InvalidOperationException($"BYOK provider '{providerName}' requires BYOK_BASE_URL."),
            };
        }

        return new ProviderConfig
        {
            Type = providerName,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        };
    }

    public sealed record RemoteRuntimeHandshake(
        string Host,
        int Port,
        string ConnectionToken,
        string? RuntimePath = null,
        DateTimeOffset StartedAtUtc = default,
        DateTimeOffset ExpiresAtUtc = default);

    public sealed record RemoteSessionState(
        string SessionId,
        string RuntimeUrl,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset ExpiresAtUtc = default,
        string? ConnectionToken = null,
        string? Prompt = null);

    private sealed record RemoteStatePointer(
        string Path,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ProtectedStateEnvelope(
        string Protection,
        string Payload);
}
