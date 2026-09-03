using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// Enough of SMTP for MailKit to complete a submission against, on a loopback port.
/// </summary>
/// <remarks>
/// A real client against a real socket, because the thing under test is a protocol conversation. A
/// mocked MailKit client would assert that we call the methods we call, which is a restatement of
/// the implementation rather than evidence that a message leaves.
///
/// It speaks plaintext only, and that is load-bearing in two directions: the success tests ask for
/// <c>Security = None</c> explicitly, and the test that the default refuses to send in the clear
/// relies on this server not advertising STARTTLS.
/// </remarks>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accepting;
    private readonly string? _authFailure;
    private readonly List<string> _messages = [];
    private readonly Lock _gate = new();

    /// <param name="authFailure">
    /// The line to answer AUTH with instead of accepting it, e.g. a 535 that quotes the credentials
    /// back. Null accepts any login.
    /// </param>
    public FakeSmtpServer(string? authFailure = null)
    {
        _authFailure = authFailure;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>The DATA payloads that completed, headers and all.</summary>
    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) return _messages.ToList(); }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ConverseAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ConverseAsync(TcpClient client)
    {
        using (client)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 fake ESMTP ready");

            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        // PLAIN only, so the exchange is one line and the test does not depend on
                        // which mechanism MailKit ranks highest.
                        await writer.WriteLineAsync("250-fake greets you");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                        break;

                    case "AUTH":
                        await writer.WriteLineAsync(_authFailure ?? "235 2.7.0 Authentication successful");
                        break;

                    case "MAIL":
                    case "RCPT":
                    case "RSET":
                    case "NOOP":
                        await writer.WriteLineAsync("250 2.1.0 Ok");
                        break;

                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var body = new StringBuilder();
                        string? data;
                        while ((data = await reader.ReadLineAsync()) is not null && data != ".")
                            body.AppendLine(data);
                        lock (_gate) _messages.Add(body.ToString());
                        await writer.WriteLineAsync("250 2.0.0 Ok: queued as fake");
                        break;

                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;

                    default:
                        await writer.WriteLineAsync("250 2.0.0 Ok");
                        break;
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try { _accepting.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
