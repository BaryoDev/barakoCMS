namespace barakoCMS.Core.Interfaces;

/// <summary>
/// What an <see cref="IEmailService"/> throws when the message was not handed over, so sending it
/// again cannot deliver it twice.
/// </summary>
/// <remarks>
/// Throw it only when nothing could have been delivered: the provider could not be reached, or it
/// refused the message before accepting it. A failure after the provider may have taken the
/// message, a timeout included, is some other exception. A caller may send the message again on
/// this one, and a second email is worse than none.
///
/// An <see cref="InvalidOperationException"/>, which is what the shipped providers threw for every
/// failure before it existed, so a caller that catches that still catches this.
/// </remarks>
public class EmailNotSentException : InvalidOperationException
{
    public EmailNotSentException(string message)
        : base(message)
    {
    }

    public EmailNotSentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
