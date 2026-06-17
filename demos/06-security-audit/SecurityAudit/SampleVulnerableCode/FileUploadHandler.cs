// UWAGA: Celowo podatny kod edukacyjny. NIE używaj w produkcji!

using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace SampleVulnerableApp;

public class FileUploadHandler
{
    // PODATNOŚĆ: Katalog uploadu bez walidacji, czy znajduje się poza web root
    private readonly string _uploadDir = "/var/app/uploads";

    // PODATNOŚĆ 1: Przechodzenie ścieżki (path traversal) — brak sanityzacji nazwy pliku od użytkownika
    // Wzorzec CVE: CWE-22 Niewłaściwe ograniczenie nazwy ścieżki (OWASP A01:2021)
    // PODATNOŚĆ 2: Brak walidacji rozszerzenia pliku
    // Wzorzec CVE: CWE-434 Nieograniczone przesyłanie plików o niebezpiecznym typie (OWASP A04:2021)
    public async Task<string> SaveFile(Stream fileStream, string userProvidedName, string contentType)
    {
        // Przechodzenie ścieżki: userProvidedName = "../../etc/cron.d/evil" → zapis poza katalogiem uploadu
        var path = Path.Combine(_uploadDir, userProvidedName);

        // Brak walidacji rozszerzenia — można przesłać: .exe, .dll, .php, .aspx
        using var outputStream = File.Create(path);
        await fileStream.CopyToAsync(outputStream);

        // PODATNOŚĆ: XXE (wstrzyknięcie zewnętrznej encji XML)
        // Wzorzec CVE: CWE-611 Niewłaściwe ograniczenie odwołań do zewnętrznych encji XML (OWASP A05:2021)
        if (contentType == "application/xml" || userProvidedName.EndsWith(".xml"))
        {
            fileStream.Seek(0, SeekOrigin.Begin);
            var content = await new StreamReader(fileStream).ReadToEndAsync();

            // XmlDocument bez wyłączonego przetwarzania DTD → XXE możliwy
            var doc = new XmlDocument();
            // doc.XmlResolver = null; // BRAKUJE tego! Umożliwia XXE
            doc.LoadXml(content);
            // Atakujący może wstrzyknąć: <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
        }

        return path;
    }

    // PODATNOŚĆ: Zip Slip — wypakowanie archiwum bez sprawdzania ścieżki
    // Wzorzec CVE: CWE-23 Przechodzenie ścieżki względnej
    public void ExtractArchive(string zipPath, string destinationDir)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // Brak sprawdzenia czy entry.FullName zawiera ".."
            // Atakujący może mieć: ../../etc/passwd jako nazwę pliku w zip
            var destPath = Path.Combine(destinationDir, entry.FullName);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    // PODATNOŚĆ: Niebezpieczna deserializacja z pełną kontrolą typu
    // Wzorzec CVE: CWE-502 Deserializacja niezaufanych danych (OWASP A08:2021)
    public void ProcessUploadedMetadata(string metadataJson)
    {
        // Deserializacja bez walidacji schematu
        // W starszych wersjach używano TypeNameHandling.All → zdalne wykonanie kodu
        var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);

        // Brak sanityzacji wartości → mogą trafić bezpośrednio do SQL/HTML
        foreach (var kvp in metadata ?? new Dictionary<string, object>())
        {
            Console.WriteLine($"Metadata: {kvp.Key} = {kvp.Value}");
        }
    }

    // PODATNOŚĆ: ReDoS (odmowa usługi przez wyrażenie regularne)
    // Wzorzec CVE: CWE-1333 Nieefektywna złożoność wyrażenia regularnego
    public bool ValidateFilename(string filename)
    {
        // Regex podatny na katastrofalny nawrót (backtracking) przy długich danych wejściowych
        var regex = new System.Text.RegularExpressions.Regex(@"^([a-zA-Z0-9]+)*\.[a-zA-Z]{3,4}$");
        return regex.IsMatch(filename);
        // Dane wejściowe: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!" → długi czas działania
    }
}
