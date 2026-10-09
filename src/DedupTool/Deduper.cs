using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DedupTool;

/// <summary>Every dedup scenario the spec defines. Classify() maps each document to exactly one.</summary>
public enum Scenario
{
    LooseFile,            // spec 3.1  root document, docType "loose"
    Email,                // spec 3.3  root document, docType "email"
    StandaloneAttachment, // spec 3.2  root document, docType "attachment" (no parent)
    FamilyMember,         // spec 3.5  anything with a parentId; follows its root's verdict
}

public enum Outcome { Kept, Suppressed }

/// <summary>Audit record: one per input document.</summary>
public sealed record Decision(string DocId, Scenario Scenario, Outcome Outcome, string Reason, string? DuplicateOf);

/// <summary>
/// Suppresses duplicate documents in a batch (spec v1.3).
///
/// Dedup is decided per FAMILY ROOT (a document with no parentId), across the whole batch (3.4):
///   - loose files:  same sha256                                         (3.1)
///   - emails:       same normalised content, NOT same sha256            (3.3)
///   - standalone attachments: same sha256                               (3.2)
/// Attachments (any depth) never get their own verdict. They inherit the verdict of their root so
/// families are always produced intact or not at all (3.5). Suppressed documents are omitted (3.6).
/// The first document seen in batch order is the one kept.
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch) => RunWithTrace(batch).Result;

    public static (DedupResult Result, IReadOnlyList<Decision> Trace) RunWithTrace(Batch batch)
    {
        // ── Step 1: index documents ─────────────────────────────────────────────
        var byId = new Dictionary<string, Document>(StringComparer.Ordinal);
        foreach (var d in batch.Documents)
        {
            if (!byId.TryAdd(d.DocId, d))
                throw new InvalidDataException($"Duplicate docId '{d.DocId}' in batch.");
        }

        // ── Step 2: resolve families (parentId chains of any depth) ─────────────
        var rootIdOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var descendants = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        foreach (var d in batch.Documents)
        {
            var root = FindRoot(d, byId);
            rootIdOf[d.DocId] = root.DocId;
            if (!ReferenceEquals(root, d))
            {
                if (!descendants.TryGetValue(root.DocId, out var list))
                    descendants[root.DocId] = list = new List<Document>();
                list.Add(d);
            }
        }

        // ── Step 3: decide a verdict for every family root ──────────────────────
        var firstByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var verdicts = new Dictionary<string, Verdict>(StringComparer.Ordinal);

        Verdict Judge(Document d, string key) =>
            firstByKey.TryAdd(key, d.DocId)
                ? new Verdict(true, null)
                : new Verdict(false, firstByKey[key]);

        foreach (var d in batch.Documents)
        {
            switch (Classify(d))
            {
                case Scenario.LooseFile:
                    // 3.1 – loose files compare by sha256 (own key space: never matches an attachment/email)
                    verdicts[d.DocId] = Judge(d, "loose|" + NormHash(d.Sha256));
                    break;

                case Scenario.Email:
                {
                    // 3.3 – emails compare by normalised content; bytes/sha256 are ignored on purpose.
                    descendants.TryGetValue(d.DocId, out var kids);
                    verdicts[d.DocId] = Judge(d, "email|" + EmailKey(d, kids));
                    break;
                }

                case Scenario.StandaloneAttachment:
                    // 3.2 – an attachment with no parent in the batch compares by sha256.
                    verdicts[d.DocId] = Judge(d, "attachment|" + NormHash(d.Sha256));
                    break;

                case Scenario.FamilyMember:
                    // 3.5 – never judged on its own; inherits its root's verdict in Step 4.
                    break;

                default:
                    throw new InvalidOperationException("Unhandled scenario.");
            }
        }

        // ── Step 4: emit rows / suppress, one decision per document ─────────────
        var rows = new List<LoadFileRow>();
        var trace = new List<Decision>();
        var suppressed = 0;

        foreach (var d in batch.Documents)
        {
            var scenario = Classify(d);
            var rootId = rootIdOf[d.DocId];
            var root = byId[rootId];
            var verdict = verdicts[rootId];
            var dup = verdict.DuplicateOf;

            var reason = scenario switch
            {
                Scenario.LooseFile => verdict.Keep
                    ? "first loose file with this sha256 (3.1)"
                    : $"same sha256 as loose file {dup} (3.1)",
                Scenario.Email => verdict.Keep
                    ? "first copy of this email (3.3)"
                    : $"same email as {dup}; sha256 ignored (3.3)",
                Scenario.StandaloneAttachment => verdict.Keep
                    ? "first standalone attachment with this sha256 (3.2)"
                    : $"same sha256 as standalone attachment {dup} (3.2)",
                Scenario.FamilyMember => verdict.Keep
                    ? $"member of kept family {rootId} (3.5)"
                    : $"member of suppressed family {rootId}, duplicate of {dup} (3.5/3.6)",
                _ => throw new InvalidOperationException("Unhandled scenario."),
            };

            if (!verdict.Keep)
            {
                suppressed++;
                trace.Add(new Decision(d.DocId, scenario, Outcome.Suppressed, reason, dup));
                continue;
            }

            rows.Add(new LoadFileRow
            {
                DocId = d.DocId,
                FamilyId = rootId,                 // family = root of the parentId chain
                ParentId = d.ParentId ?? "",
                Custodian = d.Custodian,
                DocType = d.DocType,
                FileName = d.FileName,
                // Spec: UTC 'YYYY-MM-DDTHH:MM:SSZ'; attachments carry the root email's date;
                // empty for loose files (and anything not rooted at an email).
                SentDate = Classify(root) == Scenario.Email
                    ? LoadFileWriter.NormalizeSentDate(root.SentDate)
                    : "",
                Hash = d.Sha256,
            });
            trace.Add(new Decision(d.DocId, scenario, Outcome.Kept, reason, null));
        }

        return (new DedupResult(rows, suppressed), trace);
    }

    // ── Classification ──────────────────────────────────────────────────────────

    private static Scenario Classify(Document d)
    {
        if (!string.IsNullOrEmpty(d.ParentId)) return Scenario.FamilyMember;

        return d.DocType?.Trim().ToLowerInvariant() switch
        {
            "loose" => Scenario.LooseFile,
            "email" => Scenario.Email,
            "attachment" => Scenario.StandaloneAttachment,
            _ => throw new InvalidDataException($"Document '{d.DocId}' has unknown docType '{d.DocType}'."),
        };
    }

    private static Document FindRoot(Document d, Dictionary<string, Document> byId)
    {
        var current = d;
        var hops = 0;
        while (!string.IsNullOrEmpty(current.ParentId))
        {
            if (!byId.TryGetValue(current.ParentId, out var parent))
                throw new InvalidDataException(
                    $"Document '{current.DocId}' references missing parent '{current.ParentId}'.");
            current = parent;
            if (++hops > byId.Count)
                throw new InvalidDataException($"Cyclic parentId chain at document '{d.DocId}'.");
        }
        return current;
    }

    // ── Email identity (3.3) ────────────────────────────────────────────────────

    /// <summary>
    /// Content fingerprint that is stable across Sent Items / Inbox copies:
    ///  - sent date compared as a UTC instant (offset-insensitive),
    ///  - addresses trimmed + case-insensitive, recipients order-insensitive,
    ///  - subject/body Unicode-normalised, line endings unified, trailing whitespace trimmed,
    ///  - plus the hashes of all attachments in the family (so a copy missing an attachment is
    ///    never treated as a duplicate: suppressing the other copy could lose content, 3.5).
    /// </summary>
    private static string EmailKey(Document d, List<Document>? kids)
    {
        IEnumerable<string>? toRaw = d.To;
        var recipients = (toRaw ?? Array.Empty<string>())
            .Select(NormAddr).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var childHashes = (kids ?? new List<Document>())
            .Select(k => NormHash(k.Sha256)).OrderBy(x => x, StringComparer.Ordinal);

        var raw = string.Join("\u001f",
            NormDate(d.SentDate),
            NormAddr(d.From),
            string.Join(",", recipients),
            NormText(d.Subject),
            NormText(d.Body),
            string.Join(",", childHashes));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string NormHash(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static string NormAddr(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static string NormDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : s.Trim();
    }

    private static string NormText(string? s)
    {
        var text = (s ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
        return string.Join("\n", text.Split('\n').Select(l => l.TrimEnd())).Trim();
    }

    private sealed record Verdict(bool Keep, string? DuplicateOf);
}
