// -----------------------------------------------------------------------------
// File: CsvScheduleException.cs
//
// Description:
// Exception raised while parsing a single CSV field/row into a schedule job.
// Carries structured file/row/column context and converts cleanly to
// <see cref="CsvValidationError"/> for reject reports.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Row/field-level schedule CSV parse failure with structured location metadata.
/// </summary>
internal sealed class CsvScheduleException : Exception
{
    /// <summary>
    /// Creates an exception whose message embeds file, optional row, optional column, and reason.
    /// </summary>
    /// <param name="fileName">CSV file name being parsed.</param>
    /// <param name="rowNumber">Parser row when known.</param>
    /// <param name="columnName">Column name when known.</param>
    /// <param name="reason">Failure explanation.</param>
    public CsvScheduleException(string fileName, int? rowNumber, string? columnName, string reason)
        : base(FormatMessage(fileName, rowNumber, columnName, reason))
    {
        FileName = fileName;
        RowNumber = rowNumber;
        ColumnName = columnName;
        Reason = reason;
    }

    /// <summary>CSV file name being parsed.</summary>
    public string FileName { get; }

    /// <summary>Parser row number when known; otherwise null.</summary>
    public int? RowNumber { get; }

    /// <summary>Column name when known; otherwise null.</summary>
    public string? ColumnName { get; }

    /// <summary>Failure explanation (also embedded in <see cref="Exception.Message"/>).</summary>
    public string Reason { get; }

    /// <summary>Maps this exception into a retained <see cref="CsvValidationError"/>.</summary>
    /// <returns>Equivalent validation error for reject reporting.</returns>
    public CsvValidationError ToValidationError() => new()
    {
        FileName = FileName,
        RowNumber = RowNumber,
        ColumnName = ColumnName,
        Reason = Reason,
    };

    /// <summary>Builds the standard <c>file[ row N][ column 'X']: reason</c> message.</summary>
    private static string FormatMessage(string fileName, int? rowNumber, string? columnName, string reason)
    {
        string row = rowNumber is null ? string.Empty : $" row {rowNumber.Value}";
        string column = string.IsNullOrWhiteSpace(columnName) ? string.Empty : $" column '{columnName}'";
        return $"{fileName}{row}{column}: {reason}";
    }
}
