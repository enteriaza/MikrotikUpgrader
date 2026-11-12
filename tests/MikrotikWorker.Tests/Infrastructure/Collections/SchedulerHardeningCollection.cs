// -----------------------------------------------------------------------------
// File: SchedulerHardeningCollection.cs
//
// Description:
// xUnit collection definition serialising scheduler hardening tests that share process state.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Tests.Infrastructure.Collections;
/// <summary>
/// xUnit collection definition that runs every <c>[Collection("SchedulerHardening")]</c> class
/// (the scheduler concurrency hardening tests) serially and with parallelisation disabled.
/// These tests force lost-wakeup, shutdown, abort-capacity and file-move retry races through
/// scheduler run-loop hooks and <c>ControllableFileMover</c>, so overlapping runs would add
/// unrelated CPU and timing noise to their deterministic ordering. The class is a marker only
/// and has no members.
/// </summary>
[CollectionDefinition("SchedulerHardening", DisableParallelization = true)]
public sealed class SchedulerHardeningCollection;
