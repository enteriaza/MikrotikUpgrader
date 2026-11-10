namespace MikrotikWorker.Scheduling;

internal sealed class CsvScheduleException : Exception
{
    public CsvScheduleException(string fileName, int? rowNumber, string? columnName, string reason)
        : base(FormatMessage(fileName, rowNumber, columnName, reason))
    {
        FileName = fileName;
        RowNumber = rowNumber;
        ColumnName = columnName;
        Reason = reason;
    }

    public string FileName { get; }

    public int? RowNumber { get; }

    public string? ColumnName { get; }

    public string Reason { get; }

    public CsvValidationError ToValidationError() => new()
    {
        FileName = FileName,
        RowNumber = RowNumber,
        ColumnName = ColumnName,
        Reason = Reason,
    };

    private static string FormatMessage(string fileName, int? rowNumber, string? columnName, string reason)
    {
        string row = rowNumber is null ? string.Empty : $" row {rowNumber.Value}";
        string column = string.IsNullOrWhiteSpace(columnName) ? string.Empty : $" column '{columnName}'";
        return $"{fileName}{row}{column}: {reason}";
    }
}
