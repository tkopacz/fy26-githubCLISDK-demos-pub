namespace CopilotSDK.Demos.Tests.Helpers;

/// <summary>
/// Wczytuje zmienne środowiskowe z pliku .env w katalogu repozytorium.
/// Wywołaj <see cref="Load"/> raz — np. w konstruktorze klasy testowej.
/// Zmienne już ustawione w środowisku mają priorytet (nie są nadpisywane).
/// </summary>
public static class DotEnvLoader
{
    private static readonly string EnvFilePath = FindEnvFile();
    private static bool _loaded;

    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(EnvFilePath)) return;

        foreach (var line in File.ReadAllLines(EnvFilePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx < 1) continue;

            var key = trimmed[..eqIdx].Trim();
            var value = trimmed[(eqIdx + 1)..].Trim();

            // Istniejące zmienne środowiskowe mają pierwszeństwo
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string FindEnvFile()
    {
        // Szukamy .env wychodząc w górę od katalogu binarki testów
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, ".env");
    }
}
