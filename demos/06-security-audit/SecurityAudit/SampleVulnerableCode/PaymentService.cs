// UWAGA: Celowo podatny kod edukacyjny. NIE używaj w produkcji!

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SampleVulnerableApp;

public class PaymentService
{
    // PODATNOŚĆ: Klucze API zakodowane na stałe w kodzie źródłowym
    // Wzorzec CVE: CWE-798 Stosowanie poświadczeń zakodowanych na stałe (OWASP A02:2021)
    private const string StripeApiKey = "sk_live_REDACTED_demo_placeholder";  // klucz produkcyjny!
    private const string WebhookSecret = "whsec_test_secret_exposed_in_repo";

    private readonly ILogger<PaymentService> _logger;

    public PaymentService(ILogger<PaymentService> logger) => _logger = logger;

    // PODATNOŚĆ: Logowanie wrażliwych danych karty płatniczej (naruszenie PCI DSS)
    // Wzorzec CVE: CWE-532 Wstawianie poufnych informacji do pliku dziennika
    public async Task<bool> ProcessPayment(string cardNumber, string cvv, decimal amount)
    {
        // NARUSZENIE PCI DSS: logowanie numeru karty i CVV
        _logger.LogInformation(
            "Processing payment for card: {CardNumber}, CVV: {CVV}, Amount: {Amount}",
            cardNumber, cvv, amount);

        // PODATNOŚĆ: Wyłączona walidacja certyfikatu SSL
        // Wzorzec CVE: CWE-295 Niewłaściwa walidacja certyfikatu
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };

        using var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {StripeApiKey}");

        var payload = JsonSerializer.Serialize(new { card = cardNumber, cvv, amount });
        var response = await httpClient.PostAsync(
            "https://api.stripe.com/v1/charges",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        return response.IsSuccessStatusCode;
    }

    // PODATNOŚĆ: Brak weryfikacji podpisu webhooka
    // Wzorzec CVE: CWE-347 Niewłaściwa weryfikacja podpisu kryptograficznego
    public void HandleStripeWebhook(string rawPayload, string stripeSignatureHeader)
    {
        // Nie weryfikujemy nagłówka Stripe-Signature!
        // Atakujący może wysyłać fałszywe zdarzenia płatności.
        var webhookEvent = JsonSerializer.Deserialize<StripeEvent>(rawPayload);
        ProcessPaymentEvent(webhookEvent!);
    }

    // PODATNOŚĆ: Deserializacja bez walidacji typu — potencjalny gadget chain
    // Wzorzec CVE: CWE-502 Deserializacja niezaufanych danych
    public void ProcessPaymentFromCache(string cachedJson)
    {
        // Użycie JsonSerializer jest bezpieczniejsze niż BinaryFormatter,
        // ale brak walidacji schematu/typu może prowadzić do błędów logiki
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var payment = JsonSerializer.Deserialize<dynamic>(cachedJson, options);
        // typ dynamic bez walidacji — pole amount mogłoby zostać zmienione
    }

    private void ProcessPaymentEvent(StripeEvent evt) { /* ... */ }
}

public class StripeEvent
{
    public string? Type { get; set; }
    public object? Data { get; set; }
}

// Stub dla kompilatora
public interface ILogger<T>
{
    void LogInformation(string message, params object[] args);
}
