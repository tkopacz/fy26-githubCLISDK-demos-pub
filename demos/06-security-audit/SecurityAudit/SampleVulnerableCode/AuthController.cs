// UWAGA: Celowo podatny kod edukacyjny. NIE używaj w produkcji!

using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;

namespace SampleVulnerableApp;

// PODATNOŚĆ: Brak atrybutu [Authorize] na kontrolerze — domyślnie publiczny
// Wzorzec CVE: CWE-862 Brak autoryzacji (OWASP A01:2021)
// [ApiController]
public class AuthController
{
    private readonly IUserRepository _db;

    public AuthController(IUserRepository db) => _db = db;

    // PODATNOŚĆ 1: Enumeracja użytkowników — różne komunikaty błędu ujawniają istnienie konta
    // PODATNOŚĆ 2: Brak ograniczania liczby żądań (rate limiting) → możliwy atak brute-force
    // PODATNOŚĆ 3: Porównanie hasła w postaci jawnego tekstu
    // Wzorzec CVE: CWE-307 Niewłaściwe ograniczenie nadmiernych prób uwierzytelnienia
    public object Login(LoginRequest req)
    {
        var user = _db.FindByEmail(req.Email);

        if (user == null)
            return new { error = "User not found" };  // enumeracja użytkowników!

        // Porównanie zwykłego tekstu zamiast hashowanego hasła
        if (user.Password != req.Password)  // powinno być: PasswordHasher.Verify(...)
            return new { error = "Wrong password" };

        // PODATNOŚĆ: JWT bez podpisu (algorytm: "none")
        // Wzorzec CVE: CVE-2018-1000531, CWE-347
        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim("email", req.Email),
                // PODATNOŚĆ: Rola przyznawana na podstawie adresu email!
                new Claim("role", req.Email.Contains("admin") ? "admin" : "user"),
            },
            signingCredentials: null  // null = algorytm "none" → niepodpisany JWT
        );

        return new { token = new JwtSecurityTokenHandler().WriteToken(token) };
    }

    // PODATNOŚĆ: Brak [Authorize] → każdy może wywołać endpoint administratora
    // PODATNOŚĆ: Masowe ujawnienie danych — zwraca WSZYSTKICH użytkowników bez paginacji
    // Wzorzec CVE: CWE-359 Ujawnienie informacji prywatnych (OWASP A01:2021)
    public IEnumerable<User> AdminPanel()
    {
        // Brak atrybutu [Authorize(Roles = "Admin")]
        // Zwraca wszystkich użytkowników z hasłami i danymi wrażliwymi
        return _db.GetAllUsers();
    }

    // PODATNOŚĆ: Otwarte przekierowanie (open redirect)
    // Wzorzec CVE: CWE-601 Przekierowanie URL do niezaufanej witryny
    public object Logout(string returnUrl)
    {
        // Brak walidacji returnUrl → atakujący może przekierować na witrynę phishingową
        // np. returnUrl = "https://evil.com/fake-login"
        return new { redirect = returnUrl };
    }

    // PODATNOŚĆ: Niezabezpieczone bezpośrednie odwołanie do obiektu (IDOR)
    // Wzorzec CVE: CWE-639 Obejście autoryzacji przez klucz kontrolowany przez użytkownika (OWASP A01:2021)
    public User? GetUserProfile(int userId)
    {
        // Brak weryfikacji czy userId należy do zalogowanego użytkownika
        return _db.GetById(userId);
        // Atakujący zmienia userId=1, 2, 3... i widzi profile innych użytkowników
    }
}

public record LoginRequest(string Email, string Password);

public interface IUserRepository
{
    User? FindByEmail(string email);
    User? GetById(int id);
    IEnumerable<User> GetAllUsers();
}
