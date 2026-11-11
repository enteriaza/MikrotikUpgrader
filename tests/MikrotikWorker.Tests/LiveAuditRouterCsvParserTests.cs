using System.Net;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests;

public sealed class LiveAuditRouterCsvParserTests
{
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

    private static LiveAuditRouterCsvParser.ParseResult Parse(string csv)
    {
        using StringReader reader = new(csv);
        return LiveAuditRouterCsvParser.Parse(reader);
    }
}
