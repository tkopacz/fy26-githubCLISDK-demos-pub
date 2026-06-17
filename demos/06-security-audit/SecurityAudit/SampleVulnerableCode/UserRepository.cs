// UWAGA: Ten plik CELOWO zawiera podatności bezpieczeństwa do celów edukacyjnych.
// Jest on analizowany przez Demo 06 (SecurityAudit). NIE używaj w produkcji!

using System.Data.SqlClient;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;

namespace SampleVulnerableApp;

public class UserRepository
{
    // PODATNOŚĆ: Connection string z hasłem zakodowany na stałe w kodzie źródłowym
    // Wzorzec CVE: CWE-798 Użycie zakodowanych na stałe danych uwierzytelniających
    private readonly string _connString =
        "Server=prod-db.company.com;Database=Users;Password=Sup3rS3cr3t!;User=sa";

    // PODATNOŚĆ: Wstrzyknięcie SQL (SQL Injection) przez konkatenację łańcucha
    // Wzorzec CVE: CWE-89 Wstrzyknięcie SQL (OWASP A03:2021)
    public User? GetByUsername(string username)
    {
        var query = $"SELECT * FROM Users WHERE Username = '{username}'";
        using var conn = new SqlConnection(_connString);
        return conn.QueryFirstOrDefault<User>(query);
        // Atakujący może wpisać: admin' OR '1'='1
        // Lub: admin'; DROP TABLE Users; --
    }

    // PODATNOŚĆ: Słabe haszowanie (MD5) + atak czasowy (timing attack)
    // Wzorzec CVE: CWE-916 Użycie skrótu hasła przy niewystarczającym wysiłku obliczeniowym
    public bool VerifyPassword(string storedHash, string inputPassword)
    {
        using var md5 = MD5.Create();
        var hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(inputPassword)));
        // Atak czasowy: operator == nie działa w stałym czasie
        return storedHash == hash;
        // Poprawnie: CryptographicOperations.FixedTimeEquals(...)
        // Poprawne hashowanie: Argon2, BCrypt lub PBKDF2
    }

    // PODATNOŚĆ: SSRF (fałszowanie żądań po stronie serwera)
    // Wzorzec CVE: CWE-918 Fałszowanie żądań po stronie serwera (OWASP A10:2021)
    public async Task<IEnumerable<User>> SearchUsersFromExternalService(string term)
    {
        // Użytkownik kontroluje `term` → może wskazać na wewnętrzne serwisy
        // np. term = "../../admin" lub term = "http://169.254.169.254/latest/meta-data/"
        var client = new HttpClient();
        var result = await client.GetStringAsync($"http://internal-user-service/{term}");
        return JsonSerializer.Deserialize<IEnumerable<User>>(result)!;
    }

    // PODATNOŚĆ: Mass Assignment / Over-posting
    // Wzorzec CVE: CWE-915 Niewłaściwie kontrolowana modyfikacja dynamicznie określanych atrybutów obiektu
    public void UpdateUser(User userFromRequest)
    {
        // Przyjmuje cały obiekt od klienta, w tym pola takie jak IsAdmin, Role itp.
        var query = "UPDATE Users SET Name=@Name, Email=@Email, IsAdmin=@IsAdmin, Role=@Role WHERE Id=@Id";
        using var conn = new SqlConnection(_connString);
        conn.Execute(query, userFromRequest);
    }
}

public class User
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }  // przechowywane jako MD5!
    public bool IsAdmin { get; set; }
    public string? Role { get; set; }
}
