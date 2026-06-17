// Demo 07 — CodeReview: Równoległa Rada Recenzyjna
//
// Pokazuje: 3 równoległe CopilotSession z różnymi SystemMessage (role specjalistyczne).
// Task.WhenAll łączy wyniki Architecture/Performance/Security reviewerów.
// Kluczowe: każda sesja ma własny model, narzędzia i historię.
// Uruchomienie: dotnet run --project demos/07-code-review/CodeReview [plik.cs | --diff < diff.patch]

using GitHub.Copilot;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Code Review", "Demo 07 — GitHub Copilot SDK: Parallel Review Board");
ConsoleRenderer.Info("3 równoległe sesje: Architecture, Performance, Security reviewer.\n");

if (args.Length > 0 && args[0] is "-h" or "--help" or "/?")
{
    PrintUsage();
    return 0;
}

// Domyślny przykład: OrderService z N+1, .Result, brak transakcji
var codeToReview = await LoadCodeToReviewAsync(args);
if (codeToReview is null)
    return 1;

if (string.IsNullOrWhiteSpace(codeToReview))
{
    codeToReview = """
    // OrderService.cs — przykład kodu do recenzji (zawiera celowe problemy)
    public class OrderService
    {
        private readonly AppDbContext _db;
        private readonly IEmailService _emailService;
        private readonly IInventoryService _inventoryService;

        public OrderService(AppDbContext db, IEmailService emailService, IInventoryService inventoryService)
        {
            _db = db;
            _emailService = emailService;
            _inventoryService = inventoryService;
        }

        // Problem 1: N+1 query — osobny SELECT dla każdego zamówienia
        public List<OrderDto> GetOrdersWithItems()
        {
            var orders = _db.Orders.ToList();           // SELECT * FROM Orders
            return orders.Select(o => new OrderDto
            {
                Id = o.Id,
                CustomerEmail = o.CustomerEmail,
                Items = _db.OrderItems                  // N dodatkowych SELECT
                    .Where(i => i.OrderId == o.Id)
                    .ToList()
            }).ToList();
        }

        // Problem 2: Synchroniczny I/O blokujący thread pool (.Result)
        public string GetShippingStatus(int orderId)
        {
            return _httpClient.GetStringAsync($"/api/shipping/{orderId}").Result;  // deadlock risk!
        }

        // Problem 3: Naruszenie SRP + brak transakcji + race condition
        public void ProcessOrder(Order order)
        {
            _db.Orders.Add(order);
            _db.SaveChanges();                              // Pierwsza transakcja

            _emailService.SendConfirmation(order.CustomerEmail);  // może rzucić wyjątek
            _inventoryService.DeductStock(order.Items);            // nie cofnie zamówienia!

            _db.SaveChanges();                              // Druga transakcja — niespójna
        }

        // Problem 4: Brak stronicowania + mass data exposure
        public List<Order> GetAllOrders()
        {
            return _db.Orders.Include(o => o.Items).Include(o => o.Customer).ToList();  // OOM risk
        }
    }
    """;
    ConsoleRenderer.Warn("Używam przykładowego kodu. Podaj ścieżkę do pliku albo --diff ze stdin, aby przeanalizować własny input.");
}

// Jeden CopilotClient może utworzyć wiele niezależnych instancji CopilotSession.
// Klient jest właścicielem połączenia wykonawczego; każdy recenzent poniżej ma własną sesję,
// więc role, historia i stan ukończenia nie mieszają się między recenzentami.
await using var client = CopilotClientFactory.Create();
var model = CopilotClientFactory.GetModelId();
var provider = CopilotClientFactory.GetByokProvider();

ConsoleRenderer.Rule("Uruchamiam 3 równoległe sesje recenzentów");
var startTime = DateTime.UtcNow;

// === 3 RÓWNOLEGŁE SESJE ===
// Sesje SDK są na tyle lekkie, że można je uruchamiać jednocześnie z tego samego
// klienta. To pokazuje typowy wzorzec orkiestracji: jedna aplikacja hosta, jedno
// połączenie wykonawcze i kilka wyspecjalizowanych rolami rozmów z Copilotem.
var architectTask = ReviewAsync(client, model, provider, codeToReview,
    "architecture",
    """
    You are a senior software architect specializing in C# and .NET enterprise patterns.
    Review the code for: SOLID violations, design patterns misuse, layer separation issues,
    coupling/cohesion problems, testability. Output a structured review with specific recommendations.
    Start with: "## Architecture Review"
    """);

var performanceTask = ReviewAsync(client, model, provider, codeToReview,
    "performance",
    """
    You are a .NET performance engineer. Review the code for:
    N+1 queries, missing async/await, blocking calls (.Result/.Wait()), memory allocations,
    missing pagination, EF Core anti-patterns, thread pool starvation risks.
    Start with: "## Performance Review"
    """);

var securityTask = ReviewAsync(client, model, provider, codeToReview,
    "security",
    """
    You are an application security specialist. Review the code for:
    Injection risks, missing authorization, data exposure, insecure dependencies,
    race conditions, missing input validation, logging issues.
    Start with: "## Security Review"
    """);

// Czeka na wszystkie trzy konwersacje SDK. Każde zadanie czeka na swoje
// SessionIdleEvent wewnątrz ReviewAsync, więc Task.WhenAll kończy się dopiero, gdy
// wszyscy recenzenci zakończą swoje tury.
 var results = await Task.WhenAll(architectTask, performanceTask, securityTask);
var elapsed = DateTime.UtcNow - startTime;

// === PREZENTACJA WYNIKÓW ===
AnsiConsole.WriteLine();
ConsoleRenderer.Rule($"Wyniki (równolegle w {elapsed.TotalSeconds:F1}s)");
AnsiConsole.WriteLine();

var (archName, archResult) = results[0];
var (perfName, perfResult) = results[1];
var (secName, secResult) = results[2];

AnsiConsole.MarkupLine("[bold blue]╔══ ARCHITEKTURA ══╗[/]");
AnsiConsole.WriteLine(archResult);
AnsiConsole.WriteLine();

AnsiConsole.MarkupLine("[bold yellow]╔══ PERFORMANCE ══╗[/]");
AnsiConsole.WriteLine(perfResult);
AnsiConsole.WriteLine();

AnsiConsole.MarkupLine("[bold red]╔══ BEZPIECZEŃSTWO ══╗[/]");
AnsiConsole.WriteLine(secResult);

// Zapisz scalony raport
var reportPath = Path.Combine(Directory.GetCurrentDirectory(), "code-review-report.md");
await File.WriteAllTextAsync(reportPath,
    $"# Code Review Report\n\nGenerated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n" +
    archResult + "\n\n" + perfResult + "\n\n" + secResult);

AnsiConsole.WriteLine();
ConsoleRenderer.Success($"Raport zapisany: {reportPath}");
ConsoleRenderer.Info($"3 sesje równolegle = szybciej niż {elapsed.TotalSeconds * 3:F0}s sekwencyjnie.");

static async Task<(string name, string result)> ReviewAsync(
    CopilotClient client,
    string model,
    ProviderConfig? provider,
    string code,
    string name,
    string systemMessage)
{
    // Ten pomocnik tworzy nową sesję dla każdego recenzenta. Ponowne użycie jednej sesji
    // zmieszałoby instrukcje dotyczące architektury, wydajności i bezpieczeństwa we
    // wspólnej historii rozmów, czego ta komisja recenzyjna nie chce.
    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        // W tym demie nie ma żadnych niestandardowych narzędzi, ale zasady dotyczące uprawnień
        // nadal są częścią powierzchni sesji. ApproveAll utrzymuje porównanie ról
        // skupione na zachowaniu modelu, a nie na monitach o autoryzację.
        OnPermissionRequest = PermissionHandler.ApproveAll,
        // Tryb Replace nadaje każdej sesji odrębną personę recenzenta, zamiast
        // dołączać do domyślnych instrukcji asystenta pakietu SDK.
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = systemMessage,
        },
    });

    // SendAndWaitAsync zamienia strumień zdarzeń SDK w prosty wynik dla
    // każdego zadania recenzenta: SendAsync rozpoczyna turę, a SessionIdleEvent
    // oznacza jej zakończenie.
    var result = await SessionHelper.SendAndWaitAsync(session,
        $"Review this C# code:\n\n```csharp\n{code}\n```",
        timeout: TimeSpan.FromMinutes(3));

    return (name, result);
}

static async Task<string?> LoadCodeToReviewAsync(string[] args)
{
    if (args.Length == 0)
        return string.Empty;

    if (string.Equals(args[0], "--diff", StringComparison.OrdinalIgnoreCase))
    {
        var diff = args.Length > 1
            ? string.Join(Environment.NewLine, args.Skip(1))
            : await Console.In.ReadToEndAsync();

        if (!string.IsNullOrWhiteSpace(diff))
            return diff;

        ConsoleRenderer.Error("Opcja --diff wymaga treści diff przez stdin albo jako argument.");
        PrintUsage();
        return null;
    }

    if (args.Length > 1)
    {
        ConsoleRenderer.Error("Podaj jedną ścieżkę do pliku albo użyj --diff.");
        PrintUsage();
        return null;
    }

    var path = args[0];
    if (Directory.Exists(path))
    {
        ConsoleRenderer.Error($"Oczekiwano pliku, ale podano katalog: {path}");
        PrintUsage();
        return null;
    }

    if (!File.Exists(path))
    {
        ConsoleRenderer.Error($"Nie znaleziono pliku do recenzji: {path}");
        PrintUsage();
        return null;
    }

    return await File.ReadAllTextAsync(path);
}

static void PrintUsage()
{
    ConsoleRenderer.Info("Użycie:");
    ConsoleRenderer.Info("  dotnet run --project demos/07-code-review/CodeReview -- path\\to\\file.cs");
    ConsoleRenderer.Info("  git diff | dotnet run --project demos/07-code-review/CodeReview -- --diff");
}

return 0;
