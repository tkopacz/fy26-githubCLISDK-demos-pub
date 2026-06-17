using Spectre.Console;

namespace CopilotSDK.Demos.Shared.Rendering;

/// <summary>Wrappers Spectre.Console dla spójnego wyglądu dem.</summary>
public static class ConsoleRenderer
{
    public static void Banner(string title, string subtitle = "")
    {
        AnsiConsole.Write(new FigletText(title).Centered().Color(Color.Blue));
        if (!string.IsNullOrEmpty(subtitle))
            AnsiConsole.MarkupLine($"[dim]{subtitle.EscapeMarkup()}[/]\n");
    }

    public static void Header(string text) =>
        AnsiConsole.MarkupLine($"\n[bold underline blue]{text.EscapeMarkup()}[/]\n");

    public static void Success(string text) =>
        AnsiConsole.MarkupLine($"[bold green]✓[/] {text.EscapeMarkup()}");

    public static void Error(string text) =>
        AnsiConsole.MarkupLine($"[bold red]✗[/] {text.EscapeMarkup()}");

    public static void Info(string text) =>
        AnsiConsole.MarkupLine($"[blue]ℹ[/] {text.EscapeMarkup()}");

    public static void Warn(string text) =>
        AnsiConsole.MarkupLine($"[yellow]⚠[/] {text.EscapeMarkup()}");

    public static async Task<T> SpinnerAsync<T>(string label, Func<Task<T>> action)
    {
        T result = default!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync(label, async _ =>
            {
                result = await action();
            });
        return result;
    }

    public static async Task SpinnerAsync(string label, Func<Task> action)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync(label, async _ => await action());
    }

    public static void Table<T>(IEnumerable<T> items, params (string Header, Func<T, string> Value)[] columns)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var (header, _) in columns)
            table.AddColumn(new TableColumn($"[bold]{header.EscapeMarkup()}[/]"));

        foreach (var item in items)
        {
            var cells = columns.Select(c => c.Value(item).EscapeMarkup()).ToArray();
            table.AddRow(cells);
        }

        AnsiConsole.Write(table);
    }

    public static void Rule(string title = "") =>
        AnsiConsole.Write(new Rule(title.EscapeMarkup()).RuleStyle("dim blue"));

    public static string Prompt(string question)
    {
        AnsiConsole.Markup($"[bold cyan]{question.EscapeMarkup()}[/] ");
        return Console.ReadLine() ?? string.Empty;
    }
}
