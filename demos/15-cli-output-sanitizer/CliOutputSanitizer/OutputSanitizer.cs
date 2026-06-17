using System.Text;
using System.Text.RegularExpressions;

namespace CopilotSDK.Demos.Demos.CliOutputSanitizer;

/// <summary>
/// Czyszczenie wyjścia CLI przed wysłaniem go do modelu.
/// Działa jak mały, wbudowany "RTK" w pipeline narzędzi: mniej szumu, mniej tokenów.
/// </summary>
internal static partial class OutputSanitizer
{
    // Regex dla kodów ANSI jest krytyczny dla konferencyjnego pokazu: bez niego model dostaje śmieci.
    [GeneratedRegex(@"\u001b\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    // Linie dekoracyjne (np. z `git log --graph`) są zwykle tylko ozdobą, nie wiedzą nic o treści.
    [GeneratedRegex(@"^\s*[\-_=#*~┌┐└┘├┤┬┴─│╭╮╰╯═║╔╗╚╝]+\s*$", RegexOptions.Compiled)]
    private static partial Regex DecorativeLineRegex();

    // Nadmiarowe spacje/taby w outputach CLI są bardzo częste: `dotnet   --info` albo `git   log`.
    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex ExcessWhitespaceRegex();

    public static string Sanitize(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return string.Empty;

        // 1) Usuń kody ANSI i normalizuj znaki końca linii.
        var cleaned = AnsiRegex().Replace(rawOutput, string.Empty);
        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');

        // 2) Zostaw tylko sensowne linie, ale odetnij dekoracje i nadmiarowe białe znaki.
        var lines = cleaned
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

        // 3) Zwiń puste linie do pojedynczych sekcji; zachowaj tokeny bez utraty treści.
        var builder = new StringBuilder();
        var previousWasBlank = false;

        foreach (var rawLine in lines)
        {
            var line = ExcessWhitespaceRegex().Replace(rawLine.Trim(), " ");
            if (string.IsNullOrWhiteSpace(line) || DecorativeLineRegex().IsMatch(line))
            {
                if (!previousWasBlank)
                {
                    builder.AppendLine();
                    previousWasBlank = true;
                }

                continue;
            }

            builder.AppendLine(line);
            previousWasBlank = false;
        }

        return builder.ToString().Trim();
    }
}
