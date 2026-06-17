// Demo 04 — CustomTools: Przestarzały skaner zależności
//
// Pokazuje: AIFunctionFactory.Create z Microsoft.Extensions.AI — model sam decyduje
// kiedy i które narzędzia wywołać. 3 tools: ReadProjectFile, CheckNuGetLatestVersion,
// CheckForKnownVulnerabilities. Widoczne jako ToolExecutionStartEvent/CompleteEvent.
// Uruchomienie: dotnet run --project demos/04-custom-tools/CustomTools -- ścieżka.csproj

using System.ComponentModel;
using System.Xml.Linq;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Dependency Scanner", "Demo 04 — GitHub Copilot SDK: Custom Tools");
ConsoleRenderer.Info("Skanuj pakiety NuGet — model używa 3 własnych narzędzi C#.\n");

if (args.Length == 0)
{
    ConsoleRenderer.Error("Podaj ścieżkę do konkretnego pliku .csproj.");
    ConsoleRenderer.Info("Przykład: dotnet run --project demos/04-custom-tools/CustomTools -- demos/04-custom-tools/CustomTools/CustomTools.csproj");
    return 1;
}

var csprojPath = Path.GetFullPath(args[0]);
if (!File.Exists(csprojPath))
{
    ConsoleRenderer.Error($"Plik projektu nie istnieje: {csprojPath}");
    return 1;
}

if (!csprojPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
{
    ConsoleRenderer.Error("Argument musi wskazywać plik .csproj.");
    return 1;
}

ConsoleRenderer.Info($"Analizuję: {csprojPath}");

// Klient SDK obsługuje środowisko wykonawcze. Dostępność narzędzi nie jest konfigurowana
// na poziomie klienta, ponieważ narzędzia są specyficzne dla konwersacji; każda CopilotSession
// może udostępnić modelowi inny zestaw narzędzi.
await using var client = CopilotClientFactory.Create();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    // Wykonanie narzędzia jest w SDK operacją z bramką uprawnień. ApproveAll pozwala
    // temu edukacyjnemu demu skupić się na przepływie wywoływania funkcji; aplikacje
    // produkcyjne powinny sprawdzać PermissionRequest przed zezwoleniem na skutki uboczne.
    OnPermissionRequest = PermissionHandler.ApproveAll,
    // Narzędzia to funkcje hosta ogłaszane modelowi. SDK serializuje nazwę, opis
    // i opisy parametrów każdej AIFunction do schematu, nad którym model może
    // rozumować; to model decyduje, czy i kiedy je wywołać.
    Tools =
    [
        // Narzędzie 1: odczytuje plik XML .csproj. Lambda działa w tym procesie .NET;
        // Copilot otrzymuje zwrócony ciąg JSON dopiero po zakończeniu działania funkcji hosta.
        AIFunctionFactory.Create(
            ([Description("Absolute or relative path to .csproj file")] string path) =>
            {
                var projectPath = Path.GetFullPath(path);
                var content = File.ReadAllText(projectPath);
                var doc = XDocument.Parse(content);
                var centralVersions = LoadCentralPackageVersions(Path.GetDirectoryName(projectPath)!);
                var packages = doc.Descendants("PackageReference")
                    .Select(p =>
                    {
                        var id = p.Attribute("Include")?.Value ?? "";
                        var version = ResolvePackageVersion(p, centralVersions, out var versionSource);
                        return new
                        {
                            Id = id,
                            Version = version ?? "unknown",
                            VersionSource = versionSource,
                            CanCheckExactVersion = version is not null,
                        };
                    })
                    .Where(p => !string.IsNullOrEmpty(p.Id))
                    .ToList();
                ConsoleRenderer.Info($"Znaleziono {packages.Count} pakietów NuGet w {projectPath}");
                return System.Text.Json.JsonSerializer.Serialize(packages);
            },
            "read_project_file",
            // Ten opis jest częścią schematu narzędzia przesłanego do Copilot SDK.
            // Dobre opisy są ważne, bo model wykorzystuje je, aby zdecydować, które
            // narzędzie powinno obsłużyć każdy krok podpowiedzi.
            "Reads a .csproj file and returns list of NuGet package references with versions"),

        // Narzędzie 2: sprawdza nuget.org. Narzędzia asynchroniczne są obsługiwane; SDK
        // czeka na Task, a następnie zwraca rozwiązany wynik do modelu jako wyjście narzędzia.
        AIFunctionFactory.Create(
            async ([Description("NuGet package ID, e.g. Newtonsoft.Json")] string packageId) =>
            {
                ConsoleRenderer.Info($"Sprawdzanie najnowszej wersji pakietu {packageId} na nuget.org");
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                try
                {
                    var url = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLower()}/index.json";
                    var json = await http.GetStringAsync(url);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var versions = doc.RootElement.GetProperty("versions").EnumerateArray()
                        .Select(v => v.GetString() ?? "")
                        .Where(v => !v.Contains('-'))  // tylko stable
                        .ToList();
                    return versions.LastOrDefault() ?? "unknown";
                }
                catch
                {
                    return "unavailable";
                }
            },
            "check_nuget_latest_version",
            "Checks the latest stable version of a NuGet package from nuget.org"),

        // Narzędzie 3: sprawdza OSV.dev pod kątem luk w zabezpieczeniach. Wiele parametrów
        // jest uwidocznionych w schemacie, a atrybuty Description pomagają modelowi
        // wypełnić je wartościami z wcześniejszych wyników narzędzi.
        AIFunctionFactory.Create(
            async (
                [Description("NuGet package ID")] string packageId,
                [Description("Current version of the package")] string version) =>
            {
                ConsoleRenderer.Info($"Sprawdzanie znanych luk w zabezpieczeniach dla pakietu {packageId} w wersji {version}");
                if (string.IsNullOrWhiteSpace(version) || version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    return "Version unknown; exact CVE check skipped.";

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                try
                {
                    var body = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        version,
                        package = new { name = packageId, ecosystem = "NuGet" },
                    });
                    var response = await http.PostAsync(
                        "https://api.osv.dev/v1/query",
                        new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("vulns", out var vulns))
                        return "No known vulnerabilities";
                    var ids = vulns.EnumerateArray()
                        .Select(v => v.GetProperty("id").GetString())
                        .Take(3)
                        .ToList();
                    return ids.Count == 0 ? "No known vulnerabilities" : $"VULNERABILITIES: {string.Join(", ", ids)}";
                }
                catch
                {
                    return "OSV check unavailable";
                }
            },
            "check_for_known_vulnerabilities",
            "Checks a NuGet package version for known CVE vulnerabilities via OSV.dev"),
    ],
});

// Wywołania narzędzi są widoczne w tym samym strumieniu zdarzeń sesji co tekst
// asystenta. Ta subskrypcja sama nie uruchamia narzędzi; jedynie obserwuje
// zdarzenia SDK generowane przed i po uruchomieniu funkcji hosta.
using var eventLog = session.On<SessionEvent>(evt =>
{
    switch (evt)
    {
        case ToolExecutionStartEvent t:
            // Start oznacza, że model wybrał jedno z zarejestrowanych narzędzi
            // i wygenerował argumenty pasujące do schematu AIFunction.
            AnsiConsole.MarkupLine($"[yellow]⚙ TOOL[/] {t.Data.ToolName.Replace("[", "[[").Replace("]", "]]")}");
            break;
        case ToolExecutionCompleteEvent t:
            // Complete oznacza, że funkcja hosta zakończyła się sukcesem lub niepowodzeniem.
            // Wynik pokazany tutaj jest również przekazywany do rozmowy z Copilotem, więc
            // model może kontynuować rozumowanie na podstawie świeżych danych o zależnościach.
            var ok = t.Data.Success == true ? "[green]✓[/]" : "[red]✗[/]";
            var result = (t.Data.Result?.ToString() ?? "")[..Math.Min(80, t.Data.Result?.ToString()?.Length ?? 0)];
            AnsiConsole.MarkupLine($"   {ok} {result.Replace("[", "[[").Replace("]", "]]")}");
            break;
    }
});

AnsiConsole.WriteLine();
var report = await ConsoleRenderer.SpinnerAsync(
    "Analizuję zależności...",
    () => SessionHelper.SendAndWaitAsync(session,
        // Monit opisuje przepływ pracy, ale nie wywołuje narzędzi ręcznie.
        // Model Copilot planuje sekwencję, a SDK pośredniczy w każdym wywołaniu
        // funkcji żądanym przez model.
        $"""
        Analyze the .csproj file at path: {csprojPath}

        For each PackageReference:
        1. Read the project file to get all packages
        2. Check the latest stable version on nuget.org
        3. Check if the current version has known vulnerabilities only when CanCheckExactVersion is true
        4. If VersionSource is Unknown, mark the package as centrally managed/unknown and do not claim it is outdated or vulnerable

        Then provide a report with:
        - OUTDATED: packages where current != latest
        - VULNERABLE: packages with CVEs
        - UNKNOWN: packages without an exact resolved version
        - OK: up-to-date packages without CVEs
        - Estimated migration effort (hours)
        """));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Raport Zależności");
AnsiConsole.WriteLine(report);

return 0;

static Dictionary<string, string> LoadCentralPackageVersions(string startDirectory)
{
    var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var directory = new DirectoryInfo(startDirectory);

    while (directory is not null)
    {
        var propsPath = Path.Combine(directory.FullName, "Directory.Packages.props");
        if (File.Exists(propsPath))
        {
            var doc = XDocument.Load(propsPath);
            foreach (var packageVersion in doc.Descendants("PackageVersion"))
            {
                var id = packageVersion.Attribute("Include")?.Value ??
                         packageVersion.Attribute("Update")?.Value;
                var version = packageVersion.Attribute("Version")?.Value ??
                              packageVersion.Element("Version")?.Value;

                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                    versions[id] = version;
            }
        }

        directory = directory.Parent;
    }

    return versions;
}

static string? ResolvePackageVersion(
    XElement packageReference,
    IReadOnlyDictionary<string, string> centralVersions,
    out string versionSource)
{
    var versionOverride = packageReference.Attribute("VersionOverride")?.Value ??
                          packageReference.Element("VersionOverride")?.Value;
    if (!string.IsNullOrWhiteSpace(versionOverride))
    {
        versionSource = "VersionOverride";
        return versionOverride;
    }

    var inlineVersion = packageReference.Attribute("Version")?.Value ??
                        packageReference.Element("Version")?.Value;
    if (!string.IsNullOrWhiteSpace(inlineVersion))
    {
        versionSource = "Inline";
        return inlineVersion;
    }

    var id = packageReference.Attribute("Include")?.Value ?? "";
    if (centralVersions.TryGetValue(id, out var centralVersion))
    {
        versionSource = "Directory.Packages.props";
        return centralVersion;
    }

    versionSource = "Unknown";
    return null;
}
