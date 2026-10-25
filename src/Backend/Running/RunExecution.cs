using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Running;

public enum RunExecutionStatus
{
    Recorded,
    WatchNotFound,
    NoScriptVersion,
    /// <summary>Another Run of the Watch was recorded while this one executed, so this one was discarded.</summary>
    Superseded,
    /// <summary>A new Script version was uploaded while this Run executed, so this Run of the old one was discarded.</summary>
    ScriptReplaced,
    /// <summary>The Watch was deleted while this Run executed, so there was nothing to record it against.</summary>
    Deleted,
    /// <summary>The Run's gate turned it down once it had a Run slot, so it never executed.</summary>
    NotClaimed
}

public sealed record RunExecution(RunExecutionStatus Status, Run? Run = null);
