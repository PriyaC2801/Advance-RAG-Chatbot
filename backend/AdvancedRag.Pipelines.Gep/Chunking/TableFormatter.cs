using System.Text.RegularExpressions;

namespace AdvancedRag.Pipelines.Gep.Chunking;

/// <summary>Rows of one table that belong to one region (or to no region: world aggregates, memo items).</summary>
public sealed record TableRowGroup(string? Region, IReadOnlyList<SourceLine> Rows);

public sealed record FormattedTable(
    string Title,
    string? Unit,
    IReadOnlyList<TableRowGroup> Groups,
    IReadOnlyList<SourceLine> Notes);

/// <summary>
/// Turns extracted table rows ("India | 7.2 | 7.1 | 7.7 | 6.6 | ...") into self-describing lines
/// ("India: 2023: 7.2; 2024: 7.1; 2025e: 7.7; 2026f: 6.6; ...").
///
/// Why: once a table is cut into chunks, a chunk may not contain the header row any more, and a
/// language model reading "6.6 | 7.2 | 0.1" cannot know which number is the 2026 forecast and which
/// is the revision. Attaching the column label to every value removes that ambiguity — this is how
/// we keep the application from mixing forecast years.
///
/// Tables that list every region (Table 1.1, the statistical appendix) are split into one group per
/// region, using region rows such as "South Asia | 6.6 | ...", so each chunk covers one region.
/// </summary>
public sealed partial class TableFormatter(IReadOnlyCollection<string> regionNames)
{
    public FormattedTable Format(string title, IReadOnlyList<SourceLine> sourceRows, string? sectionRegion)
    {
        var rows = MergeWrappedLabels(sourceRows);
        string? unit = null;
        List<string>? header = null;
        string? secondGroupLabel = null;   // e.g. "Percentage-point differences from January 2026 projections"
        var pending = new List<SourceLine>(); // non-data rows not yet known to be header text or context
        var notes = new List<SourceLine>();
        var groups = new List<(string? Region, List<SourceLine> Rows)> { (sectionRegion, []) };
        var inNotes = false;

        foreach (var row in rows)
        {
            var text = row.Text.Trim();
            if (FootnoteMarker().IsMatch(text)) continue; // superscript "2" printed on its own baseline

            var cells = text.Split(" | ", StringSplitOptions.TrimEntries);

            if (TryReadHeader(cells, out var labels))
            {
                // Text just above a header row describes column groups, e.g. "Percentage-point differences ...".
                var groupText = string.Join(' ', pending.Select(p => p.Text));
                if (groupText.Contains("difference", StringComparison.OrdinalIgnoreCase)) secondGroupLabel = groupText;
                foreach (var row2 in pending.Where(p => !IsColumnGroupText(p.Text)))
                {
                    groups[^1].Rows.Add(row2 with { Text = $"({row2.Text})" });
                }
                pending.Clear();
                header = LabelColumns(labels, secondGroupLabel);
                if (cells[0].Any(char.IsLetter) && !IsYearToken(cells[0].Split(' ')[0]))
                {
                    // "Fiscal-year basis 1 | 2023/24 | ..." → context row "(Fiscal-year basis)"
                    var rowLabel = TrailingFootnoteNumber().Replace(cells[0], string.Empty);
                    groups[^1].Rows.Add(row with { Text = $"({rowLabel})" });
                }
                continue;
            }

            var isData = cells.Length >= 2 && cells.Skip(1).All(IsValue) && cells[0].Any(char.IsLetter);
            if (!isData)
            {
                if (unit is null && header is null && text.StartsWith('(')) { unit = text; continue; }
                if (inNotes || IsNoteText(text)) { inNotes = true; notes.Add(row); continue; }
                pending.Add(row);
                continue;
            }

            inNotes = false;
            FlushPending(pending, groups, ref secondGroupLabel, ref header);

            var label = TrailingFootnoteNumber().Replace(cells[0], string.Empty);
            if (sectionRegion is null && regionNames.Contains(label))
            {
                groups.Add((label, []));
            }

            groups[^1].Rows.Add(row with { Text = FormatRow(label, cells[1..], header) });
        }

        FlushPending(pending, groups, ref secondGroupLabel, ref header);

        return new FormattedTable(
            title,
            unit,
            groups.Where(g => g.Rows.Count > 0).Select(g => new TableRowGroup(g.Region, g.Rows)).ToList(),
            notes);
    }

    /// <summary>
    /// Narrow table columns wrap long row labels around the numbers, e.g. in the statistical appendix:
    ///   "Latin America and the" / "2.3 | 2.3 | ..." / "Caribbean"
    ///   "Middle East, North" / "Africa, Afghanistan, and | 2.0 | ..." / "Pakistan"
    /// Unmerged, the region row is not recognized and every country after it stays in the previous
    /// region's group (Brazil would be labelled "Europe and Central Asia"). This joins the pieces back
    /// into one data row. It only merges when the result is a known region name, or when the first
    /// piece visibly continues ("…and the", "…and") around a row of numbers with no label of its own.
    /// </summary>
    private List<SourceLine> MergeWrappedLabels(IReadOnlyList<SourceLine> rows)
    {
        var result = new List<SourceLine>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Text.Trim().Split(" | ", StringSplitOptions.TrimEntries);
            var valuesOnly = cells.Length >= 3 && cells.All(IsValue);
            var labelledData = cells.Length >= 2 && cells[0].Any(char.IsLetter) && cells.Skip(1).All(IsValue);
            if (!valuesOnly && !labelledData) { result.Add(rows[i]); continue; }

            var prev = result.Count > 0 && IsLabelOnly(result[^1].Text) ? result[^1].Text.Trim() : null;
            var next = i + 1 < rows.Count && IsLabelOnly(rows[i + 1].Text) ? rows[i + 1].Text.Trim() : null;
            var own = valuesOnly ? null : cells[0];
            var values = valuesOnly ? cells : cells[1..];

            string? label = null;
            bool usePrev = false, useNext = false;
            foreach (var (p, n) in new[] { (true, true), (true, false), (false, true) })
            {
                if ((p && prev is null) || (n && next is null)) continue;
                var candidate = Join(p ? prev : null, own, n ? next : null);
                if (regionNames.Contains(candidate)) { label = candidate; usePrev = p; useNext = n; break; }
            }
            if (label is null && valuesOnly && prev is not null && next is not null && EndsWithConnector(prev))
            {
                label = Join(prev, null, next); // "St. Vincent and the" + "Grenadines"
                usePrev = useNext = true;
            }

            if (label is null) { result.Add(rows[i]); continue; }
            if (usePrev) result.RemoveAt(result.Count - 1);
            result.Add(rows[i] with { Text = $"{label} | {string.Join(" | ", values)}" });
            if (useNext) i++; // the continuation line is now part of the label
        }
        return result;

        static string Join(params string?[] parts) => string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>A short line of words only: a piece of a wrapped row label.</summary>
    private static bool IsLabelOnly(string text)
    {
        var t = text.Trim();
        return t.Length is > 0 and <= 60 && !t.Contains(" | ", StringComparison.Ordinal) && t.Any(char.IsLetter)
               && !t.StartsWith('(') && !IsNoteText(t) && !IsColumnGroupText(t)
               && !t.StartsWith("Memo", StringComparison.OrdinalIgnoreCase)
               && !t.Split(' ').Any(IsYearToken);
    }

    private static bool EndsWithConnector(string text) =>
        Regex.IsMatch(text, @"(\band|\bthe|\bof|,)$", RegexOptions.IgnoreCase);

    /// <summary>Label-only rows between data rows: section labels inside the table, or a new column-group note.</summary>
    private void FlushPending(List<SourceLine> pending, List<(string? Region, List<SourceLine> Rows)> groups,
        ref string? secondGroupLabel, ref List<string>? header)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            var row = pending[i];
            if (row.Text.Contains("difference", StringComparison.OrdinalIgnoreCase))
            {
                // e.g. Table 1.1 switches to "Level differences from January 2026 projections" for
                // commodity prices. The phrase may wrap onto the following row(s).
                var phrase = row.Text;
                while (i + 1 < pending.Count && IsColumnGroupText(pending[i + 1].Text))
                {
                    phrase += " " + pending[++i].Text;
                }
                secondGroupLabel = phrase;
                if (header is not null) header = LabelColumns(header.Select(BaseLabel).ToList(), secondGroupLabel);
                continue;
            }
            if (row.Text.StartsWith("Memo", StringComparison.OrdinalIgnoreCase))
            {
                // Memorandum items end the region group in a cross-region table (Table 1.1's
                // "High-income countries"), but stay in the region of a regional table ("SAR excluding India").
                groups.Add((groups[0].Region, []));
            }
            groups[^1].Rows.Add(row with { Text = $"({row.Text})" });
        }
        pending.Clear();
    }

    private static string FormatRow(string label, string[] values, List<string>? header)
    {
        if (header is not null && header.Count == values.Length)
        {
            var pairs = header.Zip(values, (h, v) => $"{h}: {(v == ".." ? "n/a" : v)}");
            return $"{label}: {string.Join("; ", pairs)}";
        }
        // Column count does not line up: keep the raw row rather than guess an alignment.
        return $"{label} | {string.Join(" | ", values)}";
    }

    /// <summary>
    /// A header row consists of period labels (2023, 2025e, 2026f, 2023/24, 24Q4), optionally after
    /// a text label like "Fiscal-year basis". Cells such as "2025e 2026f" that the PDF merged are split.
    /// </summary>
    private static bool TryReadHeader(string[] cells, out List<string> labels)
    {
        var tokens = cells.SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var firstPeriod = tokens.FindIndex(IsYearToken);
        labels = firstPeriod < 0 ? [] : tokens.Skip(firstPeriod).ToList();
        return labels.Count >= 3 && labels.All(IsYearToken);
    }

    /// <summary>Repeated labels belong to the second column group: "2026f" → "2026f (Percentage-point differences ...)".</summary>
    private static List<string> LabelColumns(List<string> labels, string? secondGroupLabel)
    {
        var seen = new HashSet<string>();
        return labels
            .Select(l => seen.Add(l) ? l : $"{l} ({secondGroupLabel ?? "second column group"})")
            .ToList();
    }

    private static string BaseLabel(string label) => label.Split(' ')[0];

    private static bool IsColumnGroupText(string text) =>
        text.Contains("difference", StringComparison.OrdinalIgnoreCase)
        || text.Contains("projections", StringComparison.OrdinalIgnoreCase)
        || text is "Percentage-point" or "Annual estimates and forecasts" or "Quarterly estimates"
        || text.StartsWith("(Percent change", StringComparison.Ordinal);

    private static bool IsValue(string cell) => cell == ".." || NumericValue().IsMatch(cell);

    private static bool IsYearToken(string token) => PeriodLabel().IsMatch(token);

    private static bool IsNoteText(string text) =>
        text.StartsWith("Source", StringComparison.Ordinal)
        || text.StartsWith("Note:", StringComparison.Ordinal)
        || NumberedNote().IsMatch(text)
        || text.Length > 110;

    [GeneratedRegex(@"^(\d{4}(/\d{2})?[ef]?|\d{2}Q\d[ef]?)$")]
    private static partial Regex PeriodLabel();

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumericValue();

    [GeneratedRegex(@"^\d{1,2}(\s\d{1,2})*$")]
    private static partial Regex FootnoteMarker();

    [GeneratedRegex(@"(?<=\p{L}\)?)\s\d{1,2}(\s\d{1,2})*$")]
    private static partial Regex TrailingFootnoteNumber();

    [GeneratedRegex(@"^(\d{1,2}|[a-z])\.\s")]
    private static partial Regex NumberedNote();
}
