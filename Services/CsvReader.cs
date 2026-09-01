using System.Text;

namespace Fixlosophy.Services;

/// <summary>
/// A small RFC 4180 CSV reader, written rather than taken as a dependency: the project
/// carries three NuGet packages and this needs about sixty lines. It handles the things
/// a real export from another system actually contains — quoted fields, commas and line
/// breaks inside quotes, doubled quotes as an escape, a UTF-8 BOM, and either line
/// ending — and nothing more.
///
/// Deliberately not a streaming reader: the import caps the file at a couple of
/// megabytes and needs the whole thing in memory to preview it before writing anything.
/// </summary>
public static class CsvReader
{
    /// <summary>
    /// Splits CSV text into rows of fields. A row's field count is whatever the file
    /// gave it — ragged rows are returned as-is and reconciled against the header by
    /// the caller, which can then report the row number rather than throwing.
    /// </summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;

        // Excel writes a BOM; left in place it becomes part of the first header name.
        if (text[0] == '﻿') text = text[1..];

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldWasQuoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c != '"') { field.Append(c); continue; }

                // "" inside a quoted field is a literal quote; a lone " ends the field.
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    fieldWasQuoted = true;
                    break;

                case ',':
                    row.Add(Finish(field, fieldWasQuoted));
                    fieldWasQuoted = false;
                    break;

                case '\r':
                    // Swallow the LF of a CRLF; a bare CR is still a line break.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    goto case '\n';

                case '\n':
                    row.Add(Finish(field, fieldWasQuoted));
                    fieldWasQuoted = false;
                    rows.Add([.. row]);
                    row.Clear();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        // A file not ending in a newline still has a final row.
        if (field.Length > 0 || fieldWasQuoted || row.Count > 0)
        {
            row.Add(Finish(field, fieldWasQuoted));
            rows.Add([.. row]);
        }

        // Trailing blank lines are noise, not empty customers.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace))
            rows.RemoveAt(rows.Count - 1);

        return rows;
    }

    /// Unquoted fields are trimmed — exports pad them for readability. A quoted field
    /// is returned verbatim, because the quotes are the author saying they meant it.
    private static string Finish(StringBuilder field, bool wasQuoted)
    {
        var value = field.ToString();
        field.Clear();
        return wasQuoted ? value : value.Trim();
    }
}
