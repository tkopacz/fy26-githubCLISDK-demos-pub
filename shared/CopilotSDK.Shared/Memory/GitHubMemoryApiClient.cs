using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CopilotSDK.Demos.Shared.Memory;

public interface IGitHubMemoryApiClient
{
    /// Pamięć platformy to trwały stan GitHub używany przez Copilot. Sesje SDK w wersjach
    /// demonstracyjnych tworzą/aktualizują ją poprzez zatwierdzenia PermissionRequestMemory; ten
    /// klient jest bezpośrednim pomocnikiem REST do wyświetlania/usuwania zapisanych faktów.
    Task<MemoryListResponse?> GetMemoriesAsync(CancellationToken ct = default);
    Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken ct = default);
}

/// <summary>
/// Niewielki klient REST GitHub dla pamięci platformy Copilot.
///
/// Jest to celowo oddzielone od CopilotSession: sesje wyzwalają żądania uprawnień
/// pamięci (Store/Vote) przez środowisko wykonawcze SDK, podczas gdy ten klient
/// pozwala wersjom demonstracyjnym sprawdzić lub usunąć trwałe fakty już zapisane dla użytkownika.
/// </summary>
public sealed class GitHubMemoryApiClient : IGitHubMemoryApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GitHubMemoryApiClient(string token)
        : this(CreateHttpClient(token), ownsHttp: true) { }

    // Konstruktor testowy: wstrzykuje własny HttpClient/FakeHttpHandler, dzięki czemu
    // testy jednostkowe nigdy nie dotykają rzeczywistej pamięci Copilot użytkownika.
    internal GitHubMemoryApiClient(HttpClient http, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public async Task<MemoryListResponse?> GetMemoriesAsync(CancellationToken ct = default)
    {
        // Ten punkt końcowy zwraca fakty pamięci platformy, a nie historię transkrypcji
        // sesji. Zupełnie nowa sesja CopilotSession może nadal korzystać z tych faktów, jeśli
        // środowisko wykonawcze ma dostęp do pamięci platformy.
        using var response = await _http.GetAsync("/user/copilot/memories", ct);

        // 404 oznacza, że punkt końcowy jest niedostępny lub pamięć nie jest włączona dla
        // konta/planu. Traktuj to jako „brak API pamięci”, a nie pustą listę.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MemoryListResponse>(json, JsonOpts);
    }

    public async Task<bool> DeleteMemoryAsync(string memoryId, CancellationToken ct = default)
    {
        // Usunięcie według identyfikatora usuwa trwały fakt pamięci platformy. To nie jest
        // to samo, co czyszczenie historii rozmowy CopilotSession.
        using var response = await _http.DeleteAsync($"/user/copilot/memories/{Uri.EscapeDataString(memoryId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        if (response.IsSuccessStatusCode)
            return true;

        response.EnsureSuccessStatusCode();
        return true;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static HttpClient CreateHttpClient(string token)
    {
        /// Token jest używany tylko w przypadku wywołań REST usługi GitHub z tego pomocnika. To jest
        /// nigdy nie przeszedł na model; Sesje SDK obsługują własne środowisko uwierzytelniania/wykonania
        // channel separately.
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CopilotSDK-Demo", "1.0"));
        return http;
    }
}
