using CopilotSDK.Demos.Shared.Infrastructure;
using FluentAssertions;
using GitHub.Copilot;

namespace CopilotSDK.Demos.Tests.Infrastructure;

public class CopilotClientFactoryTests : IDisposable
{
    // Fabryka odczytuje zmienne środowiskowe związane z SDK, takie jak COPILOT_MODEL
    // i BYOK_MODE. Testy tworzą migawkę tych zmiennych i przywracają je, aby jeden
    // przypadek dostawcy/modelu nie przeciekł do innego.
    private readonly Dictionary<string, string?> _savedEnv = new();

    private void SetEnv(string key, string? value)
    {
        if (!_savedEnv.ContainsKey(key))
            _savedEnv[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _savedEnv)
            Environment.SetEnvironmentVariable(key, value);
    }

    // ── GetModelId ───────────────────────────────────────────────────────────

    [Fact]
    public void GetModelId_ReturnsDefaultModelId_WhenNoEnvVarsOrSuppliedModel()
    {
        SetEnv("COPILOT_MODEL", null);
        SetEnv("BYOK_MODE", null);

        CopilotClientFactory.GetModelId().Should().Be("gpt-5.4-mini");
    }

    [Fact]
    public void GetModelId_ReturnsSuppliedModelId_WhenNoEnvVars()
    {
        SetEnv("COPILOT_MODEL", null);
        SetEnv("BYOK_MODE", null);

        CopilotClientFactory.GetModelId("gpt-4.1").Should().Be("gpt-4.1");
    }

    [Fact]
    public void GetModelId_ReturnsCopilotModelEnvVar_WhenSet()
    {
        // COPILOT_MODEL to nadpisanie modelu sesji o najwyższym priorytecie, używane
        // przez wersje demonstracyjne przed przypisaniem SessionConfig.Model.
        SetEnv("COPILOT_MODEL", " claude-3-5-sonnet ");
        SetEnv("BYOK_MODE", null);

        CopilotClientFactory.GetModelId("gpt-4.1").Should().Be("claude-3-5-sonnet");
    }

    [Fact]
    public void GetModelId_ReturnsByokDefault_WhenByokModeSetAndNoCopilotModel()
    {
        SetEnv("COPILOT_MODEL", null);
        SetEnv("BYOK_MODE", "1");

        CopilotClientFactory.GetModelId("gpt-4.1").Should().Be("gpt-4o");
    }

    [Fact]
    public void GetModelId_CopilotModelTakesPrecedenceOverByokMode()
    {
        SetEnv("COPILOT_MODEL", "my-model");
        SetEnv("BYOK_MODE", "1");

        CopilotClientFactory.GetModelId("gpt-4.1").Should().Be("my-model");
    }

    [Fact]
    public void GetModelId_IgnoresWhitespaceCopilotModel()
    {
        SetEnv("COPILOT_MODEL", "   ");
        SetEnv("BYOK_MODE", null);

        CopilotClientFactory.GetModelId("gpt-4.1").Should().Be("gpt-4.1");
    }

    // ── GetByokProvider ───────────────────────────────────────────────────────

    [Fact]
    public void GetByokProvider_ReturnsNull_WhenByokModeNotSet()
    {
        SetEnv("BYOK_MODE", null);

        CopilotClientFactory.GetByokProvider().Should().BeNull();
    }

    [Fact]
    public void GetByokProvider_ReturnsNull_WhenByokModeIsNotOne()
    {
        SetEnv("BYOK_MODE", "0");

        CopilotClientFactory.GetByokProvider().Should().BeNull();
    }

    [Fact]
    public void GetByokProvider_ReturnsOpenAiConfig_WhenByokModeSet()
    {
        // ProviderConfig jest później przekazywany do SessionConfig.Provider. Te asercje
        // zapewniają, że demo korzysta z oczekiwanego kształtu dostawcy SDK dla sesji
        // BYOK zgodnych z OpenAI.
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "openai");
        SetEnv("BYOK_API_KEY", "sk-test-key-123");
        SetEnv("BYOK_BASE_URL", null);

        var provider = CopilotClientFactory.GetByokProvider();

        provider.Should().NotBeNull();
        provider!.Type.Should().Be("openai");
        provider.ApiKey.Should().Be("sk-test-key-123");
        provider.BaseUrl.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public void GetByokProvider_ReturnsAnthropicConfig_WhenProviderIsAnthropic()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "anthropic");
        SetEnv("BYOK_API_KEY", "sk-ant-test");
        SetEnv("BYOK_BASE_URL", null);

        var provider = CopilotClientFactory.GetByokProvider();

        provider!.Type.Should().Be("anthropic");
        provider.BaseUrl.Should().Be("https://api.anthropic.com/v1");
    }

    [Fact]
    public void GetByokProvider_UsesCustomBaseUrl_WhenByokBaseUrlSet()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "openai");
        SetEnv("BYOK_API_KEY", "sk-test");
        SetEnv("BYOK_BASE_URL", "https://my-proxy.example.com/v1");

        var provider = CopilotClientFactory.GetByokProvider();

        provider!.BaseUrl.Should().Be("https://my-proxy.example.com/v1");
    }

    [Fact]
    public void GetByokProvider_ReturnsAzureConfig_WhenBaseUrlSet()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "azure");
        SetEnv("BYOK_API_KEY", "azure-test-key");
        SetEnv("BYOK_BASE_URL", "https://example.openai.azure.com/openai/deployments/demo");

        var provider = CopilotClientFactory.GetByokProvider();

        provider!.Type.Should().Be("azure");
        provider.ApiKey.Should().Be("azure-test-key");
        provider.BaseUrl.Should().Be("https://example.openai.azure.com/openai/deployments/demo");
    }

    [Fact]
    public void GetByokProvider_Throws_WhenAzureBaseUrlMissing()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "azure");
        SetEnv("BYOK_API_KEY", "azure-test-key");
        SetEnv("BYOK_BASE_URL", null);

        var act = () => CopilotClientFactory.GetByokProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BYOK_PROVIDER=azure*BYOK_BASE_URL*openai.azure.com*");
    }

    [Fact]
    public void GetByokProvider_Throws_WhenAzureBaseUrlIsWhitespace()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "azure");
        SetEnv("BYOK_API_KEY", "azure-test-key");
        SetEnv("BYOK_BASE_URL", "   ");

        var act = () => CopilotClientFactory.GetByokProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BYOK_PROVIDER=azure*BYOK_BASE_URL*openai.azure.com*");
    }

    [Fact]
    public void GetByokProvider_NormalizesProviderAndTrimsValues()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", " OpenAI ");
        SetEnv("BYOK_API_KEY", " sk-test-key ");
        SetEnv("BYOK_BASE_URL", " https://proxy.example.com/v1 ");

        var provider = CopilotClientFactory.GetByokProvider();

        provider.Should().NotBeNull();
        provider!.Type.Should().Be("openai");
        provider.ApiKey.Should().Be("sk-test-key");
        provider.BaseUrl.Should().Be("https://proxy.example.com/v1");
    }

    [Fact]
    public void GetByokProvider_UsesOpenAiAsDefault_WhenProviderNotSpecified()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", null);
        SetEnv("BYOK_API_KEY", "sk-test");
        SetEnv("BYOK_BASE_URL", null);

        var provider = CopilotClientFactory.GetByokProvider();

        provider!.Type.Should().Be("openai");
    }

    [Fact]
    public void GetByokProvider_Throws_WhenByokModeSetButApiKeyMissing()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_API_KEY", null);

        var act = () => CopilotClientFactory.GetByokProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BYOK_API_KEY*");
    }

    [Fact]
    public void GetByokProvider_Throws_WhenUnknownProvider()
    {
        SetEnv("BYOK_MODE", "1");
        SetEnv("BYOK_PROVIDER", "unknown-provider");
        SetEnv("BYOK_API_KEY", "sk-test");
        SetEnv("BYOK_BASE_URL", null);

        var act = () => CopilotClientFactory.GetByokProvider();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown-provider*");
    }

    // ── CreateServerSafeOptions ───────────────────────────────────────────────

    [Fact]
    public void CreateServerSafeOptions_UsesEmptyModeAndAppOwnedDirectories()
    {
        // Klienci bezpieczni dla serwera są używani w wersjach demonstracyjnych ASP.NET. Tryb
        // Empty i jawne katalogi uniemożliwiają środowisku wykonawczemu SDK dziedziczenie
        // dowolnego repozytorium lub konfiguracji użytkownika w scenariuszach hostowanych.
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var baseDirectory = Path.Combine(root, "copilot-home");
        var workingDirectory = Path.Combine(root, "sandbox");

        var options = CopilotClientFactory.CreateServerSafeOptions(baseDirectory, workingDirectory);

        options.Mode.Should().Be(CopilotClientMode.Empty);
        options.BaseDirectory.Should().Be(Path.GetFullPath(baseDirectory));
        options.WorkingDirectory.Should().Be(Path.GetFullPath(workingDirectory));
        options.EnableRemoteSessions.Should().BeFalse();
        options.SessionIdleTimeoutSeconds.Should().Be(300);
    }

    [Fact]
    public void CreateServerSafeOptions_UsesPrivateTelemetryPath_WhenEnabled()
    {
        // Telemetria to opcja środowiska wykonawczego SDK w CopilotClientOptions, a nie
        // ustawienie sesji. Ścieżka powinna pozostać w katalogu stanu środowiska wykonawczego
        // należącym do aplikacji, aby demo nie zapisywało śladów obok projektów użytkowników.
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var options = CopilotClientFactory.CreateServerSafeOptions(
            Path.Combine(root, "copilot-home"),
            Path.Combine(root, "sandbox"),
            enableTelemetryFile: true);

        options.Telemetry.Should().NotBeNull();
        options.Telemetry!.FilePath.Should().Be(CopilotClientFactory.TelemetryFilePath);
        options.Telemetry.FilePath.Should().StartWith(CopilotClientFactory.RuntimeStateDirectory);
        Path.GetFileName(options.Telemetry.FilePath).Should().StartWith("copilot-traces-");
        options.Telemetry.CaptureContent.Should().BeFalse();
    }

    [Fact]
    public void CreateServerSafeOptions_CapturesTelemetryContentOnlyWhenOptedIn()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var options = CopilotClientFactory.CreateServerSafeOptions(
            Path.Combine(root, "copilot-home"),
            Path.Combine(root, "sandbox"),
            enableTelemetryFile: true,
            captureTelemetryContent: true);

        options.Telemetry.Should().NotBeNull();
        options.Telemetry!.CaptureContent.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateServerSafeOptions_Throws_WhenBaseDirectoryMissing(string baseDirectory)
    {
        var act = () => CopilotClientFactory.CreateServerSafeOptions(baseDirectory, "sandbox");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("baseDirectory");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateServerSafeOptions_Throws_WhenWorkingDirectoryMissing(string workingDirectory)
    {
        var act = () => CopilotClientFactory.CreateServerSafeOptions("copilot-home", workingDirectory);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("workingDirectory");
    }
}
