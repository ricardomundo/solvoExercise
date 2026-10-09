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
