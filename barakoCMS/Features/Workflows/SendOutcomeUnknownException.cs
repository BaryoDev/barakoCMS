namespace barakoCMS.Features.Workflows;

/// <summary>
/// A send that may have gone out failed or ran out of time, and nothing says which.
/// </summary>
/// <remarks>
/// The runner records the attempt as unknown and does not retry it, because a retry of a message
/// the provider took is a second message. The message is a constant and a type name, never the
/// provider's own text, so it is safe on the run record.
/// </remarks>
internal sealed class SendOutcomeUnknownException(string message) : Exception(message)
{
    public static SendOutcomeUnknownException After(string what, string exceptionType) =>
        new($"The {what} provider failed ({exceptionType}), so it is not known whether the message was sent.");

    public static SendOutcomeUnknownException TimedOut(string what, double seconds) =>
        new($"The {what} provider did not finish within {seconds:0.#} s, so it is not known whether the {what} was sent.");
}
