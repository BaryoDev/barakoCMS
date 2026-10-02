namespace barakoCMS.Core.Interfaces;

/// <summary>
/// A unit of work could not commit because a run id, a wait key or a resume it staged was
/// committed by another unit of work first.
/// </summary>
/// <remarks>
/// Thrown from the save, not from the call that staged the key: that call was answered true
/// because nobody had committed the key yet. Nothing the unit of work held is committed. The
/// caller that wants to go on calls the member again, is answered false this time, and does what
/// it does for a key that is taken. An endpoint maps this to 409.
/// </remarks>
public sealed class DurableWorkConflictException : Exception
{
    public DurableWorkConflictException(string message)
        : base(message)
    {
    }

    public DurableWorkConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
