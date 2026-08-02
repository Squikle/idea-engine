namespace IdeaEngine.Worker;

/// <summary>
/// Process-wide single-flight gates shared by bot commands and the autopilot,
/// so scheduled and manual runs of the same operation never overlap.
/// (Research has its own coordinator in Infrastructure.)
/// </summary>
internal static class OperationGates
{
    public static readonly SemaphoreSlim Ideation = new(1, 1);
}
