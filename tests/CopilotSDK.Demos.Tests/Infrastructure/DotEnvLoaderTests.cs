using System.Reflection;
using CopilotSDK.Demos.Shared.Infrastructure;
using FluentAssertions;

namespace CopilotSDK.Demos.Tests.Infrastructure;

public sealed class DotEnvLoaderTests : IDisposable
{
    private readonly string _envPath = Path.Combine(AppContext.BaseDirectory, ".env");
    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly string? _originalEnvContent;
    private readonly bool _originalEnvExisted;

    public DotEnvLoaderTests()
    {
        _originalEnvExisted = File.Exists(_envPath);
        _originalEnvContent = _originalEnvExisted ? File.ReadAllText(_envPath) : null;
    }

    [Fact]
    public void Load_SetsVariablesFromEnvFile_WhenNotAlreadySet()
    {
        SetEnv("DOTENV_TEST_ALPHA", null);
        SetEnv("DOTENV_TEST_BETA", null);

        File.WriteAllText(_envPath, "DOTENV_TEST_ALPHA=hello\nDOTENV_TEST_BETA=world\n");
        ResetLoadedFlag();

        DotEnvLoader.Load();

        Environment.GetEnvironmentVariable("DOTENV_TEST_ALPHA").Should().Be("hello");
        Environment.GetEnvironmentVariable("DOTENV_TEST_BETA").Should().Be("world");
    }

    [Fact]
    public void Load_DoesNotOverrideExistingEnvironmentVariables()
    {
        SetEnv("DOTENV_TEST_EXISTING", "from-environment");

        File.WriteAllText(_envPath, "DOTENV_TEST_EXISTING=from-file\n");
        ResetLoadedFlag();

        DotEnvLoader.Load();

        Environment.GetEnvironmentVariable("DOTENV_TEST_EXISTING").Should().Be("from-environment");
    }

    [Fact]
    public void Load_IgnoresCommentsAndInvalidLines()
    {
        SetEnv("DOTENV_TEST_VALID", null);

        File.WriteAllText(_envPath,
            "# comment line\n" +
            "   \n" +
            "no_equals_sign\n" +
            "=missing_key\n" +
            "DOTENV_TEST_VALID=ok\n");
        ResetLoadedFlag();

        DotEnvLoader.Load();

        Environment.GetEnvironmentVariable("DOTENV_TEST_VALID").Should().Be("ok");
    }

    [Fact]
    public void Load_IsIdempotent_AfterFirstInvocation()
    {
        SetEnv("DOTENV_TEST_ONCE", null);

        File.WriteAllText(_envPath, "DOTENV_TEST_ONCE=first\n");
        ResetLoadedFlag();

        DotEnvLoader.Load();
        Environment.GetEnvironmentVariable("DOTENV_TEST_ONCE").Should().Be("first");

        SetEnv("DOTENV_TEST_ONCE", null);
        File.WriteAllText(_envPath, "DOTENV_TEST_ONCE=second\n");

        DotEnvLoader.Load();

        Environment.GetEnvironmentVariable("DOTENV_TEST_ONCE").Should().BeNull();
    }

    [Fact]
    public void Load_ParsesValueContainingAdditionalEqualsCharacters()
    {
        SetEnv("DOTENV_TEST_COMPLEX", null);

        File.WriteAllText(_envPath, "DOTENV_TEST_COMPLEX=token=abc=123\n");
        ResetLoadedFlag();

        DotEnvLoader.Load();

        Environment.GetEnvironmentVariable("DOTENV_TEST_COMPLEX").Should().Be("token=abc=123");
    }

    public void Dispose()
    {
        foreach (var (key, value) in _savedEnv)
            Environment.SetEnvironmentVariable(key, value);

        if (_originalEnvExisted)
            File.WriteAllText(_envPath, _originalEnvContent ?? string.Empty);
        else if (File.Exists(_envPath))
            File.Delete(_envPath);

        ResetLoadedFlag();
    }

    private void SetEnv(string key, string? value)
    {
        if (!_savedEnv.ContainsKey(key))
            _savedEnv[key] = Environment.GetEnvironmentVariable(key);

        Environment.SetEnvironmentVariable(key, value);
    }

    private static void ResetLoadedFlag()
    {
        var field = typeof(DotEnvLoader).GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();
        field!.SetValue(null, false);
    }
}