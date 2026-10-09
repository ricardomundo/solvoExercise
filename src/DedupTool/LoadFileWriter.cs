using System.Globalization;
using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    private const string SentDateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private static readonly char[] NeedsQuoting = { ',', '"', '\r', '\n' };

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(string.Join(",",
                Escape(r.DocId), Escape(r.FamilyId), Escape(r.ParentId), Escape(r.Custodian),
                Escape(r.DocType), Escape(r.FileName), Escape(NormalizeSentDate(r.SentDate)), Escape(r.Hash)))
              .Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Builds the whole file first, so a bad row never leaves a partial file behind.</summary>
    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        var content = ToCsv(rows);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var tmp = full + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>
    /// Spec: SentDate is UTC, exactly YYYY-MM-DDTHH:MM:SSZ (no fraction, no offset); empty when absent.
    /// Idempotent. Input must carry an offset or 'Z' (spec: "ISO 8601 with offset"); a value without
    /// one is rejected rather than silently interpreted in the server's timezone.
    /// </summary>
    public static string NormalizeSentDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            || dt.Kind == DateTimeKind.Unspecified)
        {
            throw new FormatException($"SentDate '{value}' is not an ISO 8601 timestamp with an offset.");
        }

        return dt.ToUniversalTime().ToString(SentDateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 4180: quote fields containing comma, quote or line break; double embedded quotes.</summary>
    private static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        return field.IndexOfAny(NeedsQuoting) >= 0
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }
}
