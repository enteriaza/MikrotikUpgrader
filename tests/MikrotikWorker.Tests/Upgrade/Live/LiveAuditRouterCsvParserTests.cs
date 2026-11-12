// -----------------------------------------------------------------------------
// File: LiveAuditRouterCsvParserTests.cs
//
// Description:
// Unit tests for LiveAuditRouterCsvParser validation, duplicates, and
// credential rejection.
//
// -----------------------------------------------------------------------------

using System.Net;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade.Live;
/// <summary>
/// Unit tests for live-audit CSV parsing, duplicates, and credential rejection.
/// </summary>
public sealed class LiveAuditRouterCsvParserTests
{
    /// <summary>
    /// A <c>routerip,routername</c> CSV with <c>10.0.0.1,Core-1</c> yields one valid unique row whose
    /// <c>RouterIp</c> is 10.0.0.1, whose <c>RouterName</c> is <c>Core-1</c>, and which is flagged valid.
    /// </summary>
    [Fact]
    public void Parses_ValidIPv4_AndOptionalName()
    {
        LiveAuditRouterCsvParser.ParseResult result = Parse(
            """
            routerip,routername
            10.0.0.1,Core-1
            """);

        LiveAuditRouterInput row = Assert.Single(result.ValidUnique);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), row.RouterIp);
        Assert.Equal("Core-1", row.RouterName);
        Assert.True(row.IsValid);
    }

    /// <summary>
    /// A router IP of <c>2001:db8::1</c> parses to a single valid unique row with that IPv6 address.
    /// </summary>
    [Fact]
    public void Parses_ValidIPv6()
    {
        LiveAuditRouterCsvParser.ParseResult result = Parse(
            """
            routerip
            2001:db8::1
            """);

        LiveAuditRouterInput row = Assert.Single(result.ValidUnique);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), row.RouterIp);
    }

    /// <summary>
    /// Two rows with 10.0.0.1 (named A and B) give two rows and one valid unique row. <c>DuplicateCount</c> is 1
    /// and the second row is marked <c>IsDuplicate</c>.
    /// </summary>
    [Fact]
    public void Detects_DuplicateIp()
    {
        LiveAuditRouterCsvParser.ParseResult result = Parse(
            """
            routerip,routername
            10.0.0.1,A
            10.0.0.1,B
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Single(result.ValidUnique);
        Assert.Equal(1, result.DuplicateCount);
        Assert.True(result.Rows[1].IsDuplicate);
    }

    /// <summary>
    /// Of four rows (<c>not-an-ip</c>, an empty IP, <c>10.0.0.5</c> and <c>hostname.local</c>), all four are kept,
    /// three are invalid and only <c>10.0.0.5</c> is valid unique. The validation errors mention 'not a valid IP',
    /// 'empty' and 'Hostnames'.
    /// </summary>
    [Fact]
    public void Rejects_InvalidIp_AndEmptyIp_WithoutDiscardingValidRows()
    {
        LiveAuditRouterCsvParser.ParseResult result = Parse(
            """
            routerip,routername
            not-an-ip,Bad
            ,Empty
            10.0.0.5,Good
            hostname.local,Host
            """);

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(3, result.InvalidCount);
        Assert.Single(result.ValidUnique);
        Assert.Equal("10.0.0.5", result.ValidUnique[0].RouterIpText);
        Assert.Contains(result.Rows, static r => r.ValidationError!.Contains("not a valid IP", StringComparison.Ordinal));
        Assert.Contains(result.Rows, static r => r.ValidationError!.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Rows, static r => r.ValidationError!.Contains("Hostnames", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>RouterName,RouterIp</c> header (reordered, mixed case) with the quoted values <c>"Edge, Site"</c> and
    /// <c>"10.0.0.9"</c> gives one valid row. Its name keeps the comma (<c>Edge, Site</c>) and its IP is 10.0.0.9.
    /// </summary>
    [Fact]
    public void Supports_QuotedFields_AndHeaderReordering_CaseInsensitive()
    {
        LiveAuditRouterCsvParser.ParseResult result = Parse(
            """
            RouterName,RouterIp
            "Edge, Site","10.0.0.9"
            """);

        LiveAuditRouterInput row = Assert.Single(result.ValidUnique);
        Assert.Equal("Edge, Site", row.RouterName);
        Assert.Equal(IPAddress.Parse("10.0.0.9"), row.RouterIp);
    }

    /// <summary>
    /// A CSV whose header includes <c>password</c> makes the parser throw <c>InvalidOperationException</c> with a
    /// message mentioning 'credential'.
    /// </summary>
    [Fact]
    public void Rejects_CredentialColumns()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Parse(
            """
            routerip,password
            10.0.0.1,secret
            """));

        Assert.Contains("credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parsing a temp CSV file returns its one valid row. The file's last-write time and length are unchanged
    /// afterwards.
    /// </summary>
    [Fact]
    public void ParseFile_DoesNotModifySource()
    {
        string path = Path.Combine(Path.GetTempPath(), $"live-audit-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "routerip\n10.0.0.1\n");
        DateTime before = File.GetLastWriteTimeUtc(path);
        long length = new FileInfo(path).Length;

        LiveAuditRouterCsvParser.ParseResult result = LiveAuditRouterCsvParser.ParseFile(path);

        Assert.Single(result.ValidUnique);
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Equal(length, new FileInfo(path).Length);
        File.Delete(path);
    }

    /// <summary>
    /// Wraps the CSV text in a <c>StringReader</c> and runs it through <c>LiveAuditRouterCsvParser.Parse</c>.
    /// </summary>
    private static LiveAuditRouterCsvParser.ParseResult Parse(string csv)
    {
        using StringReader reader = new(csv);
        return LiveAuditRouterCsvParser.Parse(reader);
    }
}
