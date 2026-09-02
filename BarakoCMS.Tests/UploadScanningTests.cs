using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using BarakoCMS.Files;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>Answers whatever the test scripted, and counts how often it was asked.</summary>
internal sealed class ScriptedScanner : IFileScanner
{
    private readonly ScanResult _answer;

    public ScriptedScanner(ScanResult answer, bool configured = true)
    {
        _answer = answer;
        Configured = configured;
    }

    public bool Configured { get; }

    public int Scans { get; private set; }

    /// <summary>How many bytes reached the scanner, so a test can tell a real scan from a skipped one.</summary>
    public long BytesRead { get; private set; }

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default)
    {
        Scans++;

        var buffer = new byte[8192];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            BytesRead += read;
        }

        return _answer;
    }
}

/// <summary>
/// An upload is scanned before it is stored, and a file that does not come back clean is not stored
/// at all.
/// </summary>
/// <remarks>
/// Every refusal here is paired with the same upload going through, because "it returned 4xx" is
/// satisfied just as well by an upload path that is broken, and this one is gated on a role and a
/// content type before it ever reaches a scanner.
///
/// The other half of each pairing is that nothing was stored. A refusal that still wrote the bytes
/// is the failure this whole feature exists to prevent, and the status code says nothing about it.
/// </remarks>
[Collection("Sequential")]
public class UploadScanningTests
{
    private readonly IntegrationTestFixture _factory;

    public UploadScanningTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>Not a real PNG. Nothing on this path parses it, and the scanner is scripted.</summary>
    private static byte[] Bytes() => Encoding.ASCII.GetBytes("PNG-ish bytes " + Guid.NewGuid());

    private async Task<string> AdminTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"admin-{userId}",
            Email = $"admin-{userId}@example.com",
            RoleIds = new() { role.Id },
        });
        await session.SaveChangesAsync();

        return _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString());
    }

    private (HttpClient Client, ScriptedScanner Scanner) HostWith(ScanResult answer, bool configured = true)
    {
        var scanner = new ScriptedScanner(answer, configured);

        var derived = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileScanner>();
                services.AddSingleton<IFileScanner>(scanner);
            }));

        return (derived.CreateClient(), scanner);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string token, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "pic.png");
        form.Add(new StringContent("false"), "isPublic");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/files") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<int> StoredCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<StoredFile>().CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_infected_upload_is_refused_and_nothing_is_stored()
    {
        var token = await AdminTokenAsync();
        var before = await StoredCountAsync();

        var (client, scanner) = HostWith(ScanResult.Infected("Eicar-Test-Signature"));
        var response = await UploadAsync(client, token, Bytes());

        scanner.Scans.Should().Be(1, "an upload that never reached the scanner proves nothing below");
        scanner.BytesRead.Should().BeGreaterThan(0, "and a scan handed an empty stream is not a scan");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Eicar-Test-Signature",
            "rejected with no reason is indistinguishable from a broken upload button");

        (await StoredCountAsync()).Should().Be(before, "the file must not be stored");
    }

    [Fact]
    public async Task An_upload_that_could_not_be_scanned_is_refused_and_nothing_is_stored()
    {
        var token = await AdminTokenAsync();
        var before = await StoredCountAsync();

        var (client, scanner) = HostWith(ScanResult.Unavailable("clamd is not answering."));
        var response = await UploadAsync(client, token, Bytes());

        scanner.Scans.Should().Be(1);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the scanner being down is the operator's problem, not evidence that the file is safe");

        (await StoredCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task A_clean_upload_with_a_scanner_configured_is_stored()
    {
        // The pairing. Both refusals above are also what an upload path that rejects everything
        // produces, and this endpoint is gated on a role and a content type before a scanner sees it.
        var token = await AdminTokenAsync();
        var before = await StoredCountAsync();

        var (client, scanner) = HostWith(ScanResult.Clean);
        var response = await UploadAsync(client, token, Bytes());

        scanner.Scans.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await StoredCountAsync()).Should().Be(before + 1);
    }

    [Fact]
    public async Task With_no_scanner_configured_the_upload_path_is_exactly_what_it_was()
    {
        // The compatibility guarantee, asserted rather than described. Configured false has to mean
        // the scanner is never consulted: a deployment that upgrades and sets nothing must see no
        // change at all, and a scanner that ran anyway would be one clamd outage from refusing every
        // upload on a deployment that never asked for scanning.
        var token = await AdminTokenAsync();
        var before = await StoredCountAsync();

        var (client, scanner) = HostWith(ScanResult.Infected("would refuse if asked"), configured: false);
        var response = await UploadAsync(client, token, Bytes());

        scanner.Scans.Should().Be(0, "an unconfigured scanner must not be asked");
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        (await StoredCountAsync()).Should().Be(before + 1);
    }

    [Fact]
    public async Task A_refusal_is_recorded_where_an_operator_can_see_it()
    {
        var token = await AdminTokenAsync();

        var (client, _) = HostWith(ScanResult.Infected("Eicar-Test-Signature"));
        var response = await UploadAsync(client, token, Bytes());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var scope = _factory.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<barakoCMS.Models.AuditEvent>()
            .Where(e => e.Action == "file.refused.infected")
            .ToListAsync(TestContext.Current.CancellationToken);

        events.Should().NotBeEmpty("a refusal nobody can see is a silent delete with extra steps");

        var recorded = events[^1];
        recorded.TargetType.Should().Be("file");
        recorded.Metadata.Should().ContainKey("reason");
        recorded.Metadata["reason"].ToString().Should().Contain("Eicar-Test-Signature");
        recorded.Metadata.Should().ContainKey("fileName");
    }
}

/// <summary>
/// The clamd wire format, against a socket that speaks it.
/// </summary>
/// <remarks>
/// This is the part with no second opinion. Everything above scripts the scanner, so a scanner that
/// framed its chunks wrongly, or read the reply wrongly, would pass every one of those tests and
/// then call every file clean in production. Talking to a real socket is the only thing that checks
/// the bytes.
/// </remarks>
[Collection("Sequential")]
public class ClamAvProtocolTests
{
    /// <summary>Accepts one connection, reads an INSTREAM, and answers the scripted line.</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        public FakeClamd(string reply)
        {
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

            _serving = ServeAsync(reply);
        }

        public int Port { get; }

        /// <summary>The command line the client opened with, so the test can assert on it.</summary>
        public string Command { get; private set; } = string.Empty;

        /// <summary>The bytes the client streamed, reassembled from its chunks.</summary>
        public byte[] Received { get; private set; } = Array.Empty<byte>();

        private async Task ServeAsync(string reply)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();

            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            Command = Encoding.ASCII.GetString(command);

            var body = new MemoryStream();
            var length = new byte[4];

            while (true)
            {
                await stream.ReadExactlyAsync(length);
                var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(length);
                if (size == 0) break;

                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk);
                body.Write(chunk);
            }

            Received = body.ToArray();

            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply + "\0"));
            await stream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* the test already failed */ }
            _listener.Stop();
        }
    }

    private static IFileScanner ScannerFor(int port, int timeoutSeconds = 5)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{FileScannerOptions.Section}:Address"] = $"127.0.0.1:{port}",
                [$"{FileScannerOptions.Section}:TimeoutSeconds"] = timeoutSeconds.ToString(),
            })
            .Build();

        return new ClamAvScanner(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClamAvScanner>.Instance);
    }

    [Fact]
    public async Task A_clean_answer_reads_as_clean_and_the_whole_file_reached_the_daemon()
    {
        await using var clamd = new FakeClamd("stream: OK");
        var scanner = ScannerFor(clamd.Port);

        var payload = Encoding.ASCII.GetBytes(new string('x', 200_000));
        using var content = new MemoryStream(payload);

        var result = await scanner.ScanAsync(content, TestContext.Current.CancellationToken);

        result.Verdict.Should().Be(ScanVerdict.Clean);

        // Larger than one chunk on purpose, so the framing is exercised rather than assumed. A
        // scanner that sent only the first 64 KiB would still get "OK" back from any real daemon.
        clamd.Command.Should().Be("zINSTREAM\0");
        clamd.Received.Should().Equal(payload);
    }

    [Fact]
    public async Task A_found_answer_reads_as_infected_and_keeps_the_signature_name()
    {
        await using var clamd = new FakeClamd("stream: Eicar-Test-Signature FOUND");
        var scanner = ScannerFor(clamd.Port);

        using var content = new MemoryStream(Encoding.ASCII.GetBytes("anything"));
        var result = await scanner.ScanAsync(content, TestContext.Current.CancellationToken);

        result.Verdict.Should().Be(ScanVerdict.Infected);
        result.Signature.Should().Be("Eicar-Test-Signature");
    }

    [Fact]
    public async Task An_answer_that_is_neither_reads_as_unavailable()
    {
        // clamd's own error line. Not clean: an answer nobody has verified is not a verdict.
        await using var clamd = new FakeClamd("INSTREAM size limit exceeded. ERROR");
        var scanner = ScannerFor(clamd.Port);

        using var content = new MemoryStream(Encoding.ASCII.GetBytes("anything"));
        var result = await scanner.ScanAsync(content, TestContext.Current.CancellationToken);

        result.Verdict.Should().Be(ScanVerdict.Unavailable);
        result.Error.Should().Contain("ERROR");
    }

    [Fact]
    public async Task Nothing_listening_reads_as_unavailable_rather_than_throwing()
    {
        // A port nobody is on. The upload path turns Unavailable into a refusal, so this has to be a
        // verdict rather than an exception escaping into a 500.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var dead = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var scanner = ScannerFor(dead, timeoutSeconds: 3);

        using var content = new MemoryStream(Encoding.ASCII.GetBytes("anything"));
        var result = await scanner.ScanAsync(content, TestContext.Current.CancellationToken);

        result.Verdict.Should().Be(ScanVerdict.Unavailable);
        result.Error.Should().NotBeNullOrEmpty();
    }
}
