using FastEndpoints;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http.Features;

namespace barakoCMS.Infrastructure.Http;

/// <summary>
/// Answers a request the server could not read with a 4xx instead of letting it reach the global
/// handler as a 500.
/// </summary>
/// <remarks>
/// <para>
/// Three failures are the caller's input, not a fault here: a body over Kestrel's limit, a
/// multipart body the form reader cannot parse, and a string Postgres cannot store (a NUL
/// character, SQLSTATE 22P05 or 22021). Each used to come back as a 500 whose reason was the
/// framework's or the database's own message. The reason written here is fixed text, so nothing
/// from the exception reaches the client.
/// </para>
/// <para>
/// Form failures are matched on the exception having come out of <see cref="FormFeature"/>, not on
/// its type alone. An <see cref="IOException"/> from a storage backend during an upload is a server
/// fault and must stay a 500.
/// </para>
/// </remarks>
internal sealed class MalformedRequestMiddleware(RequestDelegate next, ILogger<MalformedRequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && Classify(ex) is { } refusal)
        {
            // Status and exception type only. The method and path are caller-controlled, and the
            // request log already records them.
            logger.LogInformation(
                "Refused a malformed request with {Status}: {ExceptionType}",
                refusal.Status, ex.GetType().Name);

            context.Response.Clear();
            await new ProblemDetails(
                    [new ValidationFailure(string.Empty, refusal.Reason)],
                    context.Request.Path,
                    context.TraceIdentifier,
                    refusal.Status)
                .ExecuteAsync(context);
        }
    }

    private readonly record struct Refusal(int Status, string Reason);

    private static Refusal? Classify(Exception ex)
    {
        if (ex is BadHttpRequestException bad)
        {
            return bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? new Refusal(StatusCodes.Status413PayloadTooLarge, "The request body is too large.")
                : new Refusal(StatusCodes.Status400BadRequest, "The request could not be read.");
        }

        if (ex is InvalidDataException or IOException && CameFromFormReader(ex))
        {
            return new Refusal(StatusCodes.Status400BadRequest, "The multipart form could not be read.");
        }

        if (IsUnstorableText(ex))
        {
            return new Refusal(
                StatusCodes.Status400BadRequest, "The request contains a character that cannot be stored.");
        }

        return null;
    }

    private static bool CameFromFormReader(Exception ex)
    {
        foreach (var frame in new System.Diagnostics.StackTrace(ex).GetFrames())
        {
            for (var type = frame.GetMethod()?.DeclaringType; type is not null; type = type.DeclaringType)
            {
                if (type == typeof(FormFeature))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 22P05 is a NUL escape written into jsonb; 22021 is a NUL in a text parameter. Marten wraps
    /// the Npgsql exception at a depth that varies by command, so the chain is walked.
    /// </summary>
    private static bool IsUnstorableText(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "22P05" or "22021" })
            {
                return true;
            }
        }

        return false;
    }
}
