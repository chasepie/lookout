namespace ChasePie.Lookout.Backend.Running;

/// <summary>Whether <see cref="Runs.Run"/> admitted a Run of a Watch.</summary>
public abstract record Admission;

/// <summary>
/// The Run was admitted and executes in the background. <see cref="Finished"/> always completes: with how the Run
/// ended, cancelled if Lookout stopped before the runner answered, or faulted on an unexpected error, which whoever
/// admitted the Run reports.
/// </summary>
public sealed record Admitted(Task<RunExecution> Finished) : Admission;

/// <summary>
/// The Watch already has a Run in this process. <see cref="Gated"/> says whether that Run was admitted with a gate,
/// which the scheduler uses to recognise its own.
/// </summary>
public sealed record Refused(RunRefusal Reason, bool Gated) : Admission;

public enum RunRefusal
{
    /// <summary>The Watch's Run is waiting for a Run slot.</summary>
    WaitingForSlot,
    /// <summary>The Watch's Run has a Run slot, or gave it back while it waits for the VPN.</summary>
    Executing
}

/// <summary>Decides, once a Run has a Run slot and before the Watch is read, whether it goes ahead.</summary>
public delegate Task<bool> RunGate(CancellationToken cancellationToken);
