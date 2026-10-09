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
