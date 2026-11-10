namespace MikrotikWorker.Scheduling;

public sealed class CsvValidationError
{
    public required string FileName { get; init; }

    public int? RowNumber { get; init; }

    public string? ColumnName { get; init; }

    public required string Reason { get; init; }

    public override string ToString()
    {
        string row = RowNumber is null ? string.Empty : $" row {RowNumber.Value}";
        string column = string.IsNullOrWhiteSpace(ColumnName) ? string.Empty : $" column '{ColumnName}'";
        return $"{FileName}{row}{column}: {Reason}";
    }
}
