# AI Applied: Batch Deduplication Tool

Record of the AI-assisted work on the Batch Deduplication Tool (C# / .NET, xUnit), spec v1.3, Ingest Platform Team.
Each session below gives the prompt as submitted (typos kept) and the AI's response text. The code the AI produced is in the appendix, in its final state.

| Item | Detail |
|---|---|
| Assistant | Claude (Anthropic), claude.ai chat |
| Date | 2026-10-08 |
| Sessions | 2 (implement dedup logic, then review the load file writer) |
| Inputs supplied | `batch-sample.json`, `Deduper.cs`, `DeduperTests.cs`, `LoadFileWriter.cs`, functional spec |
| Outputs | `Deduper.cs`, `DeduperTests.cs`, `LoadFileWriter.cs`, `LoadFileWriterTests.cs` |
| Verification status | The code has **not** been compiled or run. The model classes (`Document`, `Batch`, `LoadFileRow`, `DedupResult`) were not supplied. |

---

## Session 1: Implement the deduplication logic

### Prompt

Attached files: `batch-sample.json`, `Deduper.cs`, `DeduperTests.cs`.

````text
The files I'm providing are : 1) Json with data to be processed, 2) DeDuplication Class in c# 3) Unit testing of 2.

The expected behavior is as follows:

"Batch Deduplication Tool — Functional Specification Version 1.3 · Ingest Platform Team

1. Purpose The deduplication tool reads a collection batch and writes a load file for the review platform. Its job is to make sure reviewers never see the same content twice while guaranteeing that every document they do see arrives with its full context.
2. Input A batch is a JSON file:

```json
{
  "batchId": "string",
  "documents": [ Document, ... ]
}
```

Field Type Present on Notes
`docId` string all Unique within a batch.
`custodian` string all The person whose mailbox or file share the document came from.
`docType` string all : `email`, `attachment`, or `loose`.
`parentId` string or null all For attachments, the `docId` of the containing document.
The parent is always present in the same batch. Null otherwise.
`fileName` string all Original file name.
`sha256` string all SHA-256 of the raw file bytes as collected.
`sizeBytes` integer all
`sentDate` string email ISO 8601 with offset, as recorded in the message header.
`from` string email
`to` string[] email
`subject` string email
`body` string email Plain-text body.
An attachment may itself have attachments (for example, an email attached to an email, which in turn carries a PDF). `parentId` chains express this.

3. Deduplication

3.1 Two loose files are duplicates if they have the same `sha256`.
3.2 Two attachments are duplicates if they have the same `sha256`.
3.3 The same email is routinely collected from more than one mailbox: the sender's Sent Items and each recipient's Inbox. These copies are duplicates of each other even though their raw bytes, and therefore their `sha256` values, differ (headers, encodings, and line endings vary between mailboxes). The tool must recognise them as duplicates.
3.4 Deduplication is performed across the entire batch.
3.5 An email and its attachments form a family. Families must be produced intact. A reviewer who opens an email must see every attachment that was on it.
3.6 Suppressed duplicates are omitted from the load file."

Add the missing fucntionality according to specs using a explicit control structure showing every deduplication scenario is visited
````

### AI response

I rewrote `Deduper.cs` and extended `DeduperTests.cs`. I haven't compiled or run either, because `Document`, `Batch`, `LoadFileRow` and `DedupResult` weren't in what you sent.

**What was wrong**
- Emails were compared by `sha256`, so the same email collected from two mailboxes was never suppressed (3.3).
- Attachments were deduped on their own hash, which broke families (3.5). In your sample, the forward `DOC-000004` would have lost its PDF. The recipient's copy `DOC-000009` would also have been kept as an email with no attachments.
- Nested attachments got the wrong `FamilyId` and `SentDate`, because only the direct parent was read.

**How it works now**
- Every document is classified into exactly one `Scenario` by a `switch` with an explicit case for each and a throwing `default`:
  - `LooseFile` (3.1): same `sha256` in the loose-file namespace.
  - `Email` (3.3): a content fingerprint, not the hash. It uses the sent date as a UTC instant, lower-cased addresses, order-insensitive recipients, and Unicode-normalised subject and body with `\r\n` unified and trailing spaces trimmed.
  - `StandaloneAttachment` (3.2): an attachment with no parent, deduped by `sha256`.
  - `FamilyMember` (3.5): never judged on its own, so it inherits its root's verdict. Chains of any depth work, and the order of documents in the batch doesn't matter.
- Dedup runs across the whole batch and the first document seen is kept. Suppressed documents are omitted, and the `Suppressed` count includes their attachments.
- `RunWithTrace` returns one `Decision` per document, with its scenario, outcome, reason and `DuplicateOf`. A test asserts that every scenario appears in the trace.
- `Run` keeps its original signature.
- Bad input throws `InvalidDataException`: duplicate `docId`, missing parent, parent cycle, or unknown `docType`.

**Expected result on your sample**
- Kept: DOC-1 to 8, 12, 14, 15 and 16.
- Suppressed: DOC-9, 10, 11 and 13 (4 documents).

**Calls I made where the spec is ambiguous**
1. **3.2 and 3.5 conflict.** If an attachment is suppressed because its hash was seen in another family, that family is no longer intact. I resolved it in favour of 3.5: attachments are only suppressed together with their family. Say so if you want it the other way.
2. **The email fingerprint includes the hashes of all attachments in the family.** A copy that is missing an attachment is therefore never suppressed in favour of one that has it. To drop this, remove `childHashes` from `EmailKey`.
3. **Loose files, attachments and emails are never compared across types.**
4. **`Document` fields.** `Deduper.cs` assumes `Document` has `From`, `To`, `Subject` and `Body`, and that `To` is an `IEnumerable<string>`. The new tests build batches by deserialising JSON, so they don't depend on the type of `To`.

---

## Session 2: Review the load file writer

### Prompt

Attached file: `LoadFileWriter.cs`.

````text
The class provide must follow the spec below:
"Load file A CSV with a header row. Column order and header text are fixed. The ingest job breaks if they change. Column Value `DocID` `docId` `FamilyID` `docId` of the family's top-level document. For a loose file, its own `docId`. `ParentID` `parentId`, or empty. `Custodian` `custodian` `DocType` `docType` `FileName` `fileName` `SentDate` UTC, formatted exactly `YYYY-MM-DDTHH:MM:SSZ` (e.g. `2024-03-05T19:22:00Z`; no fractional seconds, no offset). Attachments carry their family's top-level email date. Empty for loose files. `Hash` `sha256` Row order is not significant.

5.

".

Review the logic and point at the potential flaws and provide suggestion for improvement
````

### AI response

Reviewing `LoadFileWriter.cs` against the load-file spec turned up two problems in the writer. It also exposed a bug in the `Deduper` I gave you last turn, which is the most serious one.

**Flaws found**

| # | Severity | Flaw |
|---|---|---|
| 1 | **High** | **`SentDate` is never normalised.** The spec requires `YYYY-MM-DDTHH:MM:SSZ` in UTC. The writer passes the string through unchanged. My `Deduper` also copied the raw header value, such as `2024-03-05T14:22:00-05:00`, so the load file would break the ingest job. My earlier test asserted that wrong value, so it passed while the output was wrong. |
| 2 | **High** | **No CSV escaping.** A `FileName` such as `Q3 budget, final.xlsx` or one containing a `"` shifts every following column. The same applies to `Custodian`. |
| 3 | Medium | **Loose files can get a date.** The spec says `SentDate` is empty for loose files. The writer and `Deduper` emit whatever the input contains, and only emails and their attachments should carry a date. |
| 4 | Medium | **Date parsing can silently depend on the server's timezone.** A date with no offset is read as local time by default. It should be rejected instead, because the spec guarantees an offset. Output must also use `InvariantCulture`, since the `:` in a custom format is culture-sensitive. |
| 5 | Low | **Non-atomic `Write`.** A failure halfway leaves a truncated file for the ingest job to pick up. The target directory isn't created, and the encoding isn't explicit. |
| 6 | Low | **The existing test only checks the header.** Nothing covers escaping, date format or empty dates. |
| 7 | Info | **Formula injection.** A `FileName` starting with `=`, `+`, `-` or `@` runs as a formula if someone opens the CSV in Excel. I did not change the data, because the ingest job needs the exact file name. Treat the file as machine-only, or agree a convention with the ingest team. |

What is already correct: the fixed header and column order, LF endings, and `FamilyID` and `ParentID` as handled by `Deduper`.

**Fixes applied** (`LoadFileWriter.cs`, `Deduper.cs`, `DeduperTests.cs`, new `LoadFileWriterTests.cs`)

I fixed flaws 1 to 6 in `LoadFileWriter.cs`, `Deduper.cs` and the tests, and added `LoadFileWriterTests.cs`. I haven't compiled or run any of it. The column order, header text and LF endings are unchanged.

- **Dates:** `NormalizeSentDate` turns any offset timestamp into `YYYY-MM-DDTHH:MM:SSZ` in UTC and drops fractional seconds. It uses `InvariantCulture`, and running it twice gives the same result. A timestamp without an offset throws `FormatException`. `Deduper` calls it, and `ToCsv` calls it again as a safety net.
- **Loose files:** `Deduper` now emits a date only when the family root is an email, so loose files and standalone attachments always get an empty `SentDate`.
- **Escaping:** fields containing a comma, a quote or a line break are quoted, and embedded quotes are doubled.
- **`Write`:** it builds the content first, creates the directory, writes UTF-8 without a BOM to a `.tmp` file, then moves it into place.
- **Existing test:** I corrected the `DeduperTests` assertion that expected the raw `-05:00` date.

Two decisions for you or the ingest team:
1. **Dates without an offset.** I made these an error rather than assuming UTC. If your collections can contain them, switch to `DateTimeStyles.AssumeUniversal` and drop the `Unspecified` check.
2. **Formula injection (#7).** A `FileName` beginning with `=`, `+`, `-` or `@` is not neutralised, and I left it that way so the file name stays exact. If humans will open the CSV in Excel, agree a convention with the ingest team, such as a leading `'`.

---

## Reflection on AI use

- **Where it helped:** the logic for the 3.3 email fingerprint (UTC instant, case-insensitive addresses, line-ending normalisation) and the handling of nested attachments.
- **Errors made by the AI and found in review:**
  - Session 1 copied the raw `sentDate` header into the load file, which the spec forbids.
  - Session 1 also wrote a test that asserted that wrong value.
  - Session 2 caught both.
- **Judgement calls to confirm with the spec owner:**
  - The 3.2 vs 3.5 conflict, resolved in favour of intact families.
  - Whether the family's attachment hashes belong in the email fingerprint.
  - How to treat dates without an offset.
  - Formula injection.
- **Not done:** compilation and test execution, because the model classes were not available.

---

## Appendix: final source

The final state of each file produced, after both sessions.

### Deduper.cs

```csharp
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
```

### LoadFileWriter.cs

```csharp
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
```

### DeduperTests.cs

```csharp
using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static Dictionary<string, object?> Doc(string id, string cust, string type, string? parent, string hash) => new()
    {
        ["docId"] = id, ["custodian"] = cust, ["docType"] = type, ["parentId"] = parent,
        ["fileName"] = id, ["sha256"] = hash, ["sizeBytes"] = 10,
    };

    private static Dictionary<string, object?> Mail(string id, string cust, string hash, string sent,
        string from, string[] to, string subject, string body)
    {
        var d = Doc(id, cust, "email", null, hash);
        d["sentDate"] = sent; d["from"] = from; d["to"] = to; d["subject"] = subject; d["body"] = body;
        return d;
    }

    private static Dictionary<string, object?> Att(string id, string cust, string hash, string parent) =>
        Doc(id, cust, "attachment", parent, hash);

    private static Dictionary<string, object?> LooseDoc(string id, string cust, string hash) =>
        Doc(id, cust, "loose", null, hash);

    private static Batch MakeBatch(params Dictionary<string, object?>[] docs)
    {
        var json = JsonSerializer.Serialize(new { batchId = "t", documents = docs }, Json);
        return JsonSerializer.Deserialize<Batch>(json, Json)!;
    }

    private static string[] KeptIds(DedupResult r) => r.Rows.Select(x => x.DocId).ToArray();

    private static readonly string[] ToMaria = { "maria.lee@example.com" };

    // Sample-shaped batch: sender copy, recipient copy (different bytes/offset/case/line endings),
    // a forward re-using the same PDF, and a loose file held by two custodians.
    private static Batch SampleShape() => MakeBatch(
        Mail("E1", "jsmith", "h-e1", "2024-03-05T14:22:00-05:00", "john.smith@example.com", ToMaria,
             "Q3 budget review", "Hi Maria,\n\nAttached.\nThanks,\nJohn"),
        Att("A2", "jsmith", "h-xlsx", "E1"),
        Att("A3", "jsmith", "h-pdf", "E1"),
        Mail("E4", "jsmith", "h-e4", "2024-03-06T09:10:00-05:00", "john.smith@example.com",
             new[] { "ray.kim@example.com" }, "FW: vendor report", "Ray - see below."),
        Att("A5", "jsmith", "h-pdf", "E4"),                       // same hash as A3, different family
        LooseDoc("L8", "jsmith", "h-policy"),
        Mail("E9", "mlee", "h-e9", "2024-03-05T19:22:00Z", "John.Smith@example.com", ToMaria,
             "Q3 budget review", "Hi Maria,\r\n\r\nAttached.\r\nThanks,\r\nJohn"),
        Att("A10", "mlee", "h-xlsx", "E9"),
        Att("A11", "mlee", "h-pdf", "E9"),
        Mail("E12", "mlee", "h-e12", "2024-03-05T20:05:00Z", "maria.lee@example.com",
             new[] { "john.smith@example.com" }, "Re: Q3 budget review", "Approved."),
        LooseDoc("L13", "mlee", "h-policy"));

    // ── original tests ─────────────────────────────────────────────────────────

    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var batch = new Batch { Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c2", "h1") } };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch { Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c1", "h2") } };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }

    // ── 3.3 emails across mailboxes ────────────────────────────────────────────

    [Fact]
    public void SameEmailInTwoMailboxes_IsSuppressedDespiteDifferentSha256()
    {
        var result = Deduper.Run(SampleShape());

        Assert.DoesNotContain("E9", KeptIds(result));
        Assert.Contains("E1", KeptIds(result));
    }

    [Fact]
    public void ForwardedEmail_IsNotADuplicateOfTheOriginal()
    {
        var result = Deduper.Run(SampleShape());

        Assert.Contains("E4", KeptIds(result));
        Assert.Contains("E12", KeptIds(result));
    }

    [Fact]
    public void EmailWithDifferentBody_IsKept()
    {
        var batch = MakeBatch(
            Mail("E1", "a", "h1", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "one"),
            Mail("E2", "b", "h2", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "two"));

        Assert.Equal(new[] { "E1", "E2" }, KptIdsOf(batch));
    }

    private static string[] KptIdsOf(Batch b) => KeptIds(Deduper.Run(b));

    // ── 3.5 / 3.6 families ─────────────────────────────────────────────────────

    [Fact]
    public void SuppressedEmail_TakesItsWholeFamilyWithIt()
    {
        var result = Deduper.Run(SampleShape());

        Assert.DoesNotContain("A10", KeptIds(result));
        Assert.DoesNotContain("A11", KeptIds(result));
    }

    [Fact]
    public void KeptEmail_KeepsEveryAttachment_EvenWhenHashSeenInAnotherFamily()
    {
        var result = Deduper.Run(SampleShape());

        Assert.Contains("A3", KeptIds(result));
        Assert.Contains("A5", KeptIds(result)); // same sha256 as A3 but belongs to the forward
    }

    [Fact]
    public void SampleShape_ExactKeptSetAndSuppressedCount()
    {
        var result = Deduper.Run(SampleShape());

        Assert.Equal(new[] { "E1", "A2", "A3", "E4", "A5", "L8", "E12" }, KeptIds(result));
        Assert.Equal(4, result.Suppressed); // E9, A10, A11, L13
    }

    [Fact]
    public void Attachments_CarryRootFamilyIdAndSentDate()
    {
        var result = Deduper.Run(SampleShape());
        var a2 = result.Rows.Single(r => r.DocId == "A2");

        Assert.Equal("E1", a2.FamilyId);
        Assert.Equal("E1", a2.ParentId);
        Assert.Equal("2024-03-05T19:22:00Z", a2.SentDate); // UTC, root email's date
    }

    [Fact]
    public void NestedAttachments_ResolveToRootFamily_AndAreSuppressedWithIt()
    {
        var batch = MakeBatch(
            Mail("E1", "a", "h1", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"),
            Att("A2", "a", "h-eml", "E1"),
            Att("A3", "a", "h-pdf", "A2"),               // attachment of an attachment
            Mail("E4", "b", "h4", "2024-03-05T14:22:00-05:00", "X@e.com", ToMaria, "S", "b"),
            Att("A5", "b", "h-eml", "E4"),
            Att("A6", "b", "h-pdf", "A5"));

        var result = Deduper.Run(batch);

        Assert.Equal(new[] { "E1", "A2", "A3" }, KeptIds(result));
        Assert.Equal("E1", result.Rows.Single(r => r.DocId == "A3").FamilyId);
        Assert.Equal("A2", result.Rows.Single(r => r.DocId == "A3").ParentId);
        Assert.Equal(3, result.Suppressed);
    }

    [Fact]
    public void EmailCopyWithDifferentAttachments_IsNotSuppressed()
    {
        var batch = MakeBatch(
            Mail("E1", "a", "h1", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"),
            Att("A2", "a", "h-x", "E1"),
            Mail("E3", "b", "h3", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"),
            Att("A4", "b", "h-y", "E3"));

        Assert.Equal(new[] { "E1", "A2", "E3", "A4" }, KptIdsOf(batch));
    }

    // ── 3.1 / 3.2 / 3.4 hash-based types, whole-batch scope ────────────────────

    [Fact]
    public void LooseFiles_DedupAcrossCustodians()
    {
        var result = Deduper.Run(SampleShape());

        Assert.Contains("L8", KeptIds(result));
        Assert.DoesNotContain("L13", KeptIds(result));
    }

    [Fact]
    public void LooseFile_IsNotDedupedAgainstAnAttachmentWithSameHash()
    {
        var batch = MakeBatch(
            Mail("E1", "a", "h1", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"),
            Att("A2", "a", "h-same", "E1"),
            LooseDoc("L3", "a", "h-same"));

        Assert.Equal(new[] { "E1", "A2", "L3" }, KptIdsOf(batch));
    }

    [Fact]
    public void StandaloneAttachments_DedupBySha256()
    {
        var batch = MakeBatch(Doc("A1", "a", "attachment", null, "h"), Doc("A2", "b", "attachment", null, "h"));

        var result = Deduper.Run(batch);

        Assert.Equal(new[] { "A1" }, KeptIds(result));
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void ChildListedBeforeParent_StillResolvesFamily()
    {
        var batch = MakeBatch(
            Att("A2", "a", "h-x", "E1"),
            Mail("E1", "a", "h1", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"));

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal("E1", r.FamilyId));
    }

    // ── control structure: every scenario is visited ───────────────────────────

    [Fact]
    public void Trace_VisitsEveryScenario_AndEveryDocumentExactlyOnce()
    {
        var batch = MakeBatch(
            LooseDoc("L1", "a", "h-l"),
            Mail("E2", "a", "h2", "2024-03-05T19:22:00Z", "x@e.com", ToMaria, "S", "b"),
            Att("A3", "a", "h-x", "E2"),
            Doc("S4", "a", "attachment", null, "h-s"));

        var (_, trace) = Deduper.RunWithTrace(batch);

        Assert.Equal(Enum.GetValues<Scenario>().OrderBy(x => x), trace.Select(t => t.Scenario).Distinct().OrderBy(x => x));
        Assert.Equal(new[] { "L1", "E2", "A3", "S4" }, trace.Select(t => t.DocId));
    }

    [Fact]
    public void Trace_ReportsSuppressedDocsWithTheKeptOriginal()
    {
        var (_, trace) = Deduper.RunWithTrace(SampleShape());

        var e9 = trace.Single(t => t.DocId == "E9");
        var a10 = trace.Single(t => t.DocId == "A10");

        Assert.Equal(Outcome.Suppressed, e9.Outcome);
        Assert.Equal("E1", e9.DuplicateOf);
        Assert.Equal(Scenario.FamilyMember, a10.Scenario);
        Assert.Equal(Outcome.Suppressed, a10.Outcome);
    }

    // ── bad input ──────────────────────────────────────────────────────────────

    [Fact]
    public void MissingParent_Throws()
    {
        var batch = MakeBatch(Att("A1", "a", "h", "NOPE"));

        Assert.Throws<InvalidDataException>(() => Deduper.Run(batch));
    }
}
```

### LoadFileWriterTests.cs

```csharp
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class LoadFileWriterTests
{
    private static LoadFileRow Row(string fileName = "a.txt", string sentDate = "", string custodian = "c1") => new()
    {
        DocId = "D1", FamilyId = "D1", ParentId = "", Custodian = custodian,
        DocType = "email", FileName = fileName, SentDate = sentDate, Hash = "abc",
    };

    private static string[] Lines(string csv) => csv.TrimEnd('\n').Split('\n');

    [Theory]
    [InlineData("2024-03-05T14:22:00-05:00", "2024-03-05T19:22:00Z")]   // offset -> UTC
    [InlineData("2024-03-05T19:22:00Z", "2024-03-05T19:22:00Z")]        // already UTC (idempotent)
    [InlineData("2024-03-05T19:22:00.987Z", "2024-03-05T19:22:00Z")]    // fraction dropped
    [InlineData("2024-03-06T01:30:00+05:30", "2024-03-05T20:00:00Z")]   // crosses midnight
    [InlineData("", "")]
    public void SentDate_IsUtcWithoutFractionOrOffset(string input, string expected)
    {
        var line = Lines(LoadFileWriter.ToCsv(new[] { Row(sentDate: input) }))[1];

        Assert.Equal($"D1,D1,,c1,email,a.txt,{expected},abc", line);
    }

    [Fact]
    public void SentDate_WithoutOffset_IsRejected()
    {
        Assert.Throws<FormatException>(() => LoadFileWriter.NormalizeSentDate("2024-03-05T14:22:00"));
    }

    [Fact]
    public void FileNameWithCommaAndQuote_IsQuotedAndEscaped()
    {
        var line = Lines(LoadFileWriter.ToCsv(new[] { Row(fileName: "Q3, \"final\".xlsx") }))[1];

        Assert.Equal("D1,D1,,c1,email,\"Q3, \"\"final\"\".xlsx\",,abc", line);
    }

    [Fact]
    public void Output_UsesLfOnly_AndKeepsHeaderFirst()
    {
        var csv = LoadFileWriter.ToCsv(new[] { Row(), Row() });

        Assert.DoesNotContain('\r', csv);
        Assert.StartsWith(LoadFileWriter.Header + "\n", csv);
    }

    [Fact]
    public void LooseFile_GetsEmptySentDate_FromDeduper()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                new Document { DocId = "L1", Custodian = "c", DocType = "loose", FileName = "n.txt", Sha256 = "h", SizeBytes = 1,
                               SentDate = "2024-03-05T14:22:00-05:00" }, // bogus input must not leak out
            },
        };

        var row = Deduper.Run(batch).Rows.Single();

        Assert.Equal("", row.SentDate);
        Assert.Equal("L1", row.FamilyId);
    }

    [Fact]
    public void Write_CreatesDirectory_AndLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "out");
        var path = Path.Combine(dir, "load.csv");
        try
        {
            LoadFileWriter.Write(path, new[] { Row() });

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }
}
```

