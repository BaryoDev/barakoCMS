namespace barakoCMS.Features.Numbering.Next;

internal sealed record NextNumberRequest(string ContentType);

/// <param name="Series">The series the number is from.</param>
/// <param name="Number">The formatted number, to send as the numbering field of the new entry.</param>
/// <param name="Value">The number without prefix or padding.</param>
internal sealed record NextNumberResponse(string Series, string Number, long Value);
