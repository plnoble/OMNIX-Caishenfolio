using System.Text;

namespace Caishenfolio.Host.Portfolio;

/// <summary>
/// Minimal RFC 4180 reader/writer for the statement files a personal ledger deals with.
/// Handles quoted fields, embedded separators and newlines, doubled quotes, and a UTF-8 BOM —
/// all of which appear in real broker exports.
/// </summary>
public static class DelimitedText
{
    /// <summary>Splits text into rows of fields. Auto-detects comma vs tab from the header line.</summary>
    public static IReadOnlyList<string[]> Parse(string text, char? separator = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        text = text.TrimStart('﻿');
        var delimiter = separator ?? DetectSeparator(text);

        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var sawAnyChar = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    sawAnyChar = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    if (sawAnyChar || fields.Count > 1 || fields[0].Length > 0)
                    {
                        rows.Add(fields.ToArray());
                    }

                    fields.Clear();
                    sawAnyChar = false;
                    break;
                default:
                    if (c == delimiter)
                    {
                        fields.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                        sawAnyChar = true;
                    }

                    break;
            }
        }

        fields.Add(field.ToString());
        if (sawAnyChar || fields.Count > 1)
        {
            rows.Add(fields.ToArray());
        }

        return rows;
    }

    /// <summary>Writes rows as CSV, quoting only where required.</summary>
    public static string Write(IEnumerable<IEnumerable<string>> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        value ??= "";
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static char DetectSeparator(string text)
    {
        var newline = text.IndexOf('\n');
        var header = newline >= 0 ? text[..newline] : text;
        return header.Count(c => c == '\t') > header.Count(c => c == ',') ? '\t' : ',';
    }
}
