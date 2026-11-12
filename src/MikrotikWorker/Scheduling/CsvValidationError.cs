// -----------------------------------------------------------------------------
// File: CsvValidationError.cs
//
// Description:
// Immutable description of a single CSV validation failure used in reject reports
// and parse results. Carries optional row/column context and formats a stable
// human-readable message via <see cref="ToString"/>.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// One retained validation problem discovered while accepting a scheduled CSV.
/// </summary>
public sealed class CsvValidationError
{
    /// <summary>Source CSV file name (not necessarily a full path).</summary>
    public required string FileName { get; init; }

    /// <summary>1-based parser row number when the error is row-scoped; otherwise null.</summary>
    public int? RowNumber { get; init; }

    /// <summary>CSV column name when the error is column-scoped; otherwise null.</summary>
    public string? ColumnName { get; init; }

    /// <summary>Human-readable explanation of the failure (never contains credentials).</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Formats <c>{FileName}[ row N][ column 'X']: {Reason}</c> for reject reports and logs.
    /// </summary>
    public override string ToString()
    {
        string row = RowNumber is null ? string.Empty : $" row {RowNumber.Value}";
        string column = string.IsNullOrWhiteSpace(ColumnName) ? string.Empty : $" column '{ColumnName}'";
        return $"{FileName}{row}{column}: {Reason}";
    }
}
