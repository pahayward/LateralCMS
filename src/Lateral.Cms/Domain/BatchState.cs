namespace Lateral.Cms.Domain;

/// <summary>Where a batch has got to.</summary>
public enum BatchState
{
    /// <summary>Accepted and stored; some events are waiting to be applied (their outcome is <c>queued</c>).</summary>
    Queued,

    /// <summary>Every event has its final outcome.</summary>
    Completed,

    /// <summary>The worker gave up on the batch. It is kept, and an admin can requeue it.</summary>
    Failed,
}
