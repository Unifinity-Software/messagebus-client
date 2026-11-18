using Spectre.Console;

namespace MessageBus.Cli.Output;

/// <summary>How a summary value reads: plain, good (green), a problem (red), worth a look (yellow), or quiet (grey).</summary>
public enum SummaryTone
{
    /// <summary>Plain.</summary>
    Plain,
    /// <summary>Green.</summary>
    Good,
    /// <summary>Red.</summary>
    Bad,
    /// <summary>Yellow.</summary>
    Note,
    /// <summary>Grey.</summary>
    Quiet,
}

/// <summary>One line of a summary: what, and its value; the value's first word or phrase may carry a tone.</summary>
/// <param name="Label">What it is ("Server").</param>
/// <param name="Value">Its value.</param>
/// <param name="Tone">How the <paramref name="Lead"/> (or, without one, the whole value) reads.</param>
/// <param name="Lead">A short word in front of the value in the tone's colour ("ready", "off").</param>
public sealed record SummaryRow(string Label, string Value, SummaryTone Tone = SummaryTone.Plain, string? Lead = null);

/// <summary>
/// The start-up summary of <c>mbus runner</c> and the top of <c>mbus doctor</c> (the original issue), as a two-column grid: labels on
/// the left, values on the right, wrapping to the terminal's width.
/// </summary>
public static class SummaryView
{
    /// <summary>Writes <paramref name="rows"/> under <paramref name="title"/>.</summary>
    public static void Write(IAnsiConsole console, string title, IEnumerable<SummaryRow> rows)
    {
        console.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        if (!console.Profile.Out.IsTerminal)
        {
            // A file or another program: plain "Label  value" lines, without a grid's padding to the width.
            var list = rows.ToList();
            var width = list.Count == 0 ? 0 : list.Max(r => r.Label.Length);
            var indent = "\n" + new string(' ', width + 2);
            foreach (var row in list)
            {
                var value = row.Lead is { } lead ? (row.Value.Length == 0 ? lead : $"{lead} {row.Value}") : row.Value;
                console.Profile.Out.Writer.WriteLine($"{row.Label.PadRight(width)}  {value.Replace("\n", indent, StringComparison.Ordinal)}".TrimEnd());
            }
            console.WriteLine();
            return;
        }
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        foreach (var row in rows)
        {
            grid.AddRow(new Markup($"[grey]{Markup.Escape(row.Label)}[/]"), new Markup(Value(row)));
        }
        console.Write(grid);
        console.WriteLine();
    }

    private static string Value(SummaryRow row)
    {
        var colour = row.Tone switch
        {
            SummaryTone.Good => "green",
            SummaryTone.Bad => "red",
            SummaryTone.Note => "yellow",
            SummaryTone.Quiet => "grey",
            _ => null,
        };
        if (row.Lead is { } lead)
        {
            var head = colour is null ? Markup.Escape(lead) : $"[{colour}]{Markup.Escape(lead)}[/]";
            return row.Value.Length == 0 ? head : $"{head} {Markup.Escape(row.Value)}";
        }
        return colour is null ? Markup.Escape(row.Value) : $"[{colour}]{Markup.Escape(row.Value)}[/]";
    }
}
