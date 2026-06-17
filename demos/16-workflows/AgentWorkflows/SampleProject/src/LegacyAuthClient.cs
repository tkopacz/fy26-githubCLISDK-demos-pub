using System.Text;

namespace Acme.Tickets;

public sealed class LegacyAuthClient(HttpClient httpClient)
{
    public async Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        // HACK: jest to celowo uproszczone na potrzeby danych wejściowych demo.
        var payload = new StringContent($"token={token}", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await httpClient.PostAsync("/legacy/auth/validate", payload, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
