using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

public sealed class ScheduledJobCsvParserTests
{
    private static readonly DateTime UtcNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ValidCsv_IsAccepted_WithDifferentColumnOrderAndCase()
    {
        string path = await WriteTempCsvAsync(
            """
            ROUTERIP,RouterName,datestop,DATESTART
            192.168.1.1,CORE-01,02/12/2026 18:00:00,01/12/2026 08:00:00
            192.168.1.2,CORE-02,02/12/2026 18:00:00,01/12/2026 08:00:00
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted);
            Assert.Equal(2, result.RecordCount);
            Assert.Equal(new DateTime(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc), result.DateStartUtc);
            Assert.Equal(new DateTime(2026, 12, 2, 16, 0, 0, DateTimeKind.Utc), result.DateStopUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task QuotedFields_AreParsed()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            "01/12/2026 08:00:00","02/12/2026 18:00:00","CORE,01","192.168.1.10"
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted);

            List<MikrotikScheduledJob> jobs = [];
            Guid importId = Guid.NewGuid();
            await foreach (MikrotikScheduledJob job in CreateParser().EnumerateJobsAsync(path, importId, CancellationToken.None))
            {
                jobs.Add(job);
            }

            MikrotikScheduledJob single = Assert.Single(jobs);
            Assert.Equal("CORE,01", single.RouterName);
            Assert.Equal(importId, single.ImportId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingRequiredHeader_IsRejected()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.ColumnName == "routerip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyRequiredValue_IsRejected()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.ColumnName == "routername");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AdditionalColumns_AreIgnored()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip,notes,owner
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1,ignore-me,ops
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CsvWithoutExecutionMode_IsAccepted()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted);

            List<MikrotikScheduledJob> jobs = [];
            await foreach (MikrotikScheduledJob job in CreateParser().EnumerateJobsAsync(path, Guid.NewGuid(), CancellationToken.None))
            {
                jobs.Add(job);
            }

            MikrotikScheduledJob single = Assert.Single(jobs);
            Assert.Equal("CORE-01", single.RouterName);
            Assert.Equal(IPAddress.Parse("192.168.1.1"), single.RouterIp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("executionmode")]
    [InlineData("EXECUTIONMODE")]
    [InlineData("ExecutionMode")]
    public async Task LegacyExecutionModeColumn_IsRejected(string header)
    {
        string path = await WriteTempCsvAsync(
            $"""
            datestart,datestop,routername,routerip,{header}
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1,FullUpgrade
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(
                result.Errors,
                static e => e.ColumnName == "executionmode"
                    && e.Reason.Contains("no longer supported", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("2001:db8::1")]
    public async Task ValidIpAddresses_AreAccepted(string ip)
    {
        string path = await WriteTempCsvAsync(
            $"""
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,{ip}
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted, string.Join("; ", result.Errors));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("router.example.com")]
    [InlineData("CORE-01")]
    public async Task InvalidIpOrHostname_IsRejected(string ip)
    {
        string path = await WriteTempCsvAsync(
            $"""
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,{ip}
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.ColumnName == "routerip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnZaCulture_ParsesDayMonthYear()
    {
        // 01/12/2026 must be 1 December, not 12 January.
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00,02/12/2026 18:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted);
            Assert.Equal(12, result.DateStartUtc.Month);
            Assert.Equal(1, result.DateStartUtc.Day);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidDate_IsRejected()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            31/02/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.ColumnName == "datestart");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FutureStart_FutureStop_IsAccepted()
    {
        // UtcNow = 2026-01-01 00:00Z; both bounds are later.
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted, string.Join("; ", result.Errors.Select(static e => e.Reason)));
            Assert.Equal(new DateTime(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc), result.DateStartUtc);
            Assert.Equal(new DateTime(2026, 12, 2, 16, 0, 0, DateTimeKind.Utc), result.DateStopUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PastStart_FutureStop_IsAccepted_AsActiveWindow()
    {
        // Start 2020 (past), stop Dec 2026 (future) relative to UtcNow 2026-01-01.
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2020 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted, string.Join("; ", result.Errors.Select(static e => e.Reason)));
            Assert.True(result.DateStartUtc <= UtcNow);
            Assert.True(UtcNow < result.DateStopUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StartEqualToNow_FutureStop_IsAccepted()
    {
        // UtcNow 2026-01-01 00:00Z = 01/01/2026 02:00 Africa/Johannesburg.
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/01/2026 02:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.True(result.IsAccepted, string.Join("; ", result.Errors.Select(static e => e.Reason)));
            Assert.Equal(UtcNow, result.DateStartUtc);
            Assert.True(UtcNow < result.DateStopUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PastStart_StopEqualToNow_IsRejectedAsExpired()
    {
        // Stop exactly UtcNow (exclusive end → expired); start earlier the same local day.
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/01/2026 01:00:00,01/01/2026 02:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(
                result.Errors,
                static e => e.ColumnName == "datestop"
                    && e.Reason.Contains("expired", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PastStart_PastStop_IsRejectedAsExpired()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2020 08:00:00,02/12/2020 18:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(
                result.Errors,
                static e => e.ColumnName == "datestop"
                    && e.Reason.Contains("expired", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DateStopEarlierThanDateStart_IsRejected()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            02/12/2026 18:00:00,01/12/2026 08:00:00,CORE-01,192.168.1.1
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.Reason.Contains("later than datestart", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public async Task InconsistentWindowsAcrossRows_AreRejected()
    {
        string path = await WriteTempCsvAsync(
            """
            datestart,datestop,routername,routerip
            01/12/2026 08:00:00,02/12/2026 18:00:00,CORE-01,192.168.1.1
            01/12/2026 09:00:00,02/12/2026 18:00:00,CORE-02,192.168.1.2
            """);

        try
        {
            CsvParseResult result = await CreateParser().ValidateAsync(path, UtcNow, CancellationToken.None);
            Assert.False(result.IsAccepted);
            Assert.Contains(result.Errors, static e => e.Reason.Contains("same datestart", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TimeZoneConversion_UsesConfiguredZoneNotServerLocal()
    {
        CsvDateTimeParser parser = CreateDateTimeParser();
        DateTime utc = parser.ParseToUtc("01/12/2026 08:00:00", "demo.csv", 2, "datestart");

        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal(new DateTime(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public async Task EnumerateJobs_DoesNotBufferEntireFile()
    {
        StringWriter writer = new(CultureInfo.InvariantCulture);
        writer.WriteLine("datestart,datestop,routername,routerip");
        for (int i = 0; i < 250; i++)
        {
            writer.WriteLine($"01/12/2026 08:00:00,02/12/2026 18:00:00,R{i},10.0.0.{i % 200 + 1}");
        }

        string path = await WriteTempCsvAsync(writer.ToString());
        try
        {
            int count = 0;
            await foreach (MikrotikScheduledJob _ in CreateParser().EnumerateJobsAsync(path, Guid.NewGuid(), CancellationToken.None))
            {
                count++;
                if (count == 10)
                {
                    break;
                }
            }

            Assert.Equal(10, count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ScheduledJobCsvParser CreateParser() =>
        new(
            Options.Create(new ScheduledJobsOptions()),
            CreateDateTimeParser());

    private static CsvDateTimeParser CreateDateTimeParser() =>
        new(Options.Create(new CsvOptions
        {
            Culture = "en-ZA",
            TimeZoneId = "Africa/Johannesburg",
            DateTimeFormats =
            [
                "dd/MM/yyyy HH:mm:ss",
                "dd/MM/yyyy HH:mm",
                "d/M/yyyy H:mm:ss",
                "d/M/yyyy H:mm",
                "yyyy/MM/dd HH:mm:ss",
                "yyyy/MM/dd HH:mm",
            ],
        }));

    private static async Task<string> WriteTempCsvAsync(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"jobs-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, contents);
        return path;
    }
}
