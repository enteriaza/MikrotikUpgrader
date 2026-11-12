// -----------------------------------------------------------------------------
// File: SerilogLoggingCollection.cs
//
// Description:
// xUnit collection definition serialising SerilogLoggingTests that mutate static Log.Logger.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Infrastructure.Collections;
/// <summary>
/// xUnit collection definition that runs every <c>[Collection("Serilog")]</c> class (Serilog
/// configuration, production-host logging and upgrade device-log tests) serially and with
/// parallelisation disabled. Those tests replace or flush the process-wide static
/// <c>Log.Logger</c>, so concurrent runs would capture each other's events or write to a disposed
/// logger. The class is a marker only and has no members.
/// </summary>
[CollectionDefinition("Serilog", DisableParallelization = true)]
public sealed class SerilogLoggingCollection;
