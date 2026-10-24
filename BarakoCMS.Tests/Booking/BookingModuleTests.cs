using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Booking;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Booking;

/// <summary>
/// The Booking module: the overlap rule inside the write, resource management behind a capability,
/// and the public availability route.
/// </summary>
/// <remarks>
/// The fixture points the module at <c>bookingprobe</c>. Every test books its own resource, so
/// bookings in one test never meet another's.
/// </remarks>
[Collection("Sequential")]
public class BookingModuleTests
{
    private const string TypeName = "bookingprobe";

    private readonly IntegrationTestFixture _factory;

    public BookingModuleTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typeDeclared;

    private async Task DeclareTypeAsync()
    {
        await TypeGate.WaitAsync(Ct);
        try
        {
            if (_typeDeclared)
            {
                return;
            }

            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == TypeName, Ct))
            {
                session.Store(new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = TypeName,
                    DisplayName = "Booking probe",
                    Fields =
                    [
                        new FieldDefinition { Name = "Resource", DisplayName = "Resource", Type = "string" },
                        new FieldDefinition { Name = "Start", DisplayName = "Start", Type = "string" },
                        new FieldDefinition { Name = "End", DisplayName = "End", Type = "string" },
                        new FieldDefinition { Name = "Status", DisplayName = "Status", Type = "string" },
                        new FieldDefinition { Name = "Customer", DisplayName = "Customer", Type = "string" },
                    ],
                });
                await session.SaveChangesAsync(Ct);
            }

            _typeDeclared = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private async Task<HttpClient> ClientAsync(params string[] roles)
    {
        await DeclareTypeAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync(roles));
        return client;
    }

    private Task<HttpClient> AdminAsync() => ClientAsync("SuperAdmin", "Admin");

    private static object ResourceBody(
        string name = "Room",
        string zone = "UTC",
        int? slot = 60,
        object[]? hours = null,
        object[]? exceptions = null) => new
        {
            name,
            timeZone = zone,
            slotMinutes = slot,
            active = true,
            openingHours = hours ?? Enumerable.Range(0, 7)
                .Select(d => (object)new { day = ((DayOfWeek)d).ToString(), start = "09:00", end = "17:00" }).ToArray(),
            exceptions = exceptions ?? [],
        };

    private static async Task<Guid> CreateResourceAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/booking/resources", body, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.Created, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    private static readonly DateTimeOffset Day = new(2031, 6, 2, 0, 0, 0, TimeSpan.Zero);

    private static Task<HttpResponseMessage> BookAsync(
        HttpClient client, Guid resource, double fromHour, double toHour, string? status = null) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = TypeName,
            data = new Dictionary<string, object?>
            {
                ["Resource"] = resource.ToString(),
                ["Start"] = Day.AddHours(fromHour).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["End"] = Day.AddHours(toHour).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Status"] = status,
            },
        }, Ct);

    private static async Task<Guid> BookedAsync(HttpClient client, Guid resource, double fromHour, double toHour)
    {
        var res = await BookAsync(client, resource, fromHour, toHour);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue(text);
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<List<Content>> StoredBookingsAsync(Guid resource)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var all = await session.Query<Content>().Where(c => c.ContentType == TypeName).ToListAsync(Ct);
        return all.Where(c => c.Data.TryGetValue("Resource", out var r) && r.ToString() == resource.ToString()).ToList();
    }

    private async Task<JsonElement> AvailabilityAsync(Guid resource, DateTimeOffset from, DateTimeOffset to)
    {
        var url = $"/api/public/availability?resource={resource}"
                  + $"&from={Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm:sszzz"))}"
                  + $"&to={Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm:sszzz"))}";
        var res = await _factory.CreateClient().GetAsync(url, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Two_concurrent_requests_for_the_same_slot_give_exactly_one_booking()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => BookAsync(client, resource, 10, 11)));

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1, "the lock serialises the writes, so only the first is accepted");
        results.Where(r => !r.IsSuccessStatusCode).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);

        var stored = await StoredBookingsAsync(resource);
        stored.Should().NotBeEmpty();
        stored.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_overlapping_booking_is_refused_without_saying_who_holds_the_time()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());
        await BookedAsync(client, resource, 10, 12);

        var res = await BookAsync(client, resource, 11, 13);
        var text = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("not available");
        text.Should().NotContain("10:00").And.NotContain("12:00");
    }

    [Fact]
    public async Task Bookings_far_outside_the_requested_time_do_not_block_it_and_offsets_near_the_edge_still_do()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());
        await BookedAsync(client, resource, 24 * 40 + 10, 24 * 40 + 12);
        await BookedAsync(client, resource, -24 * 40 + 10, -24 * 40 + 12);

        (await BookAsync(client, resource, 10, 12)).IsSuccessStatusCode.Should().BeTrue("neither far booking overlaps");

        // 2031-06-03T08:00+14:00 to 10:00+14:00 is 2031-06-02T18:00Z to 20:00Z, a calendar day later in its own offset.
        var edge = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = TypeName,
            data = new Dictionary<string, object?>
            {
                ["Resource"] = resource.ToString(),
                ["Start"] = "2031-06-03T08:00:00+14:00",
                ["End"] = "2031-06-03T10:00:00+14:00",
            },
        }, Ct);
        edge.IsSuccessStatusCode.Should().BeTrue(await edge.Content.ReadAsStringAsync(Ct));

        var clash = await BookAsync(client, resource, 19, 21);

        clash.StatusCode.Should().Be(HttpStatusCode.BadRequest, await clash.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_booking_stored_with_a_padded_or_non_canonical_date_still_blocks_an_overlap()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());

        async Task StoreAsync(string start, string end)
        {
            var res = await client.PostAsJsonAsync("/api/contents", new
            {
                contentType = TypeName,
                data = new Dictionary<string, object?>
                {
                    ["Resource"] = resource.ToString(),
                    ["Start"] = start,
                    ["End"] = end,
                },
            }, Ct);
            res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(Ct));
        }

        await StoreAsync("  2031-06-02T10:00:00Z", "2031-06-02T12:00:00Z");
        await StoreAsync("2031-06-02 14:00:00+00:00", "2031-06-02T16:00:00.000+00:00");

        var first = await BookAsync(client, resource, 11, 13);
        var second = await BookAsync(client, resource, 15, 17);

        first.StatusCode.Should().Be(HttpStatusCode.BadRequest, await first.Content.ReadAsStringAsync(Ct));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, await second.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Adjacent_bookings_of_one_resource_are_allowed()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());

        await BookedAsync(client, resource, 10, 11);
        await BookedAsync(client, resource, 11, 12);
        await BookedAsync(client, resource, 9, 10);

        (await StoredBookingsAsync(resource)).Should().HaveCount(3);
    }

    [Fact]
    public async Task The_same_time_on_another_resource_is_allowed()
    {
        var client = await AdminAsync();
        var one = await CreateResourceAsync(client, ResourceBody("One"));
        var two = await CreateResourceAsync(client, ResourceBody("Two"));

        await BookedAsync(client, one, 10, 11);
        await BookedAsync(client, two, 10, 11);
    }

    [Fact]
    public async Task A_cancelled_booking_frees_its_slot()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());
        var first = await BookedAsync(client, resource, 10, 11);
        (await BookAsync(client, resource, 10, 11)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var cancel = await client.PutAsJsonAsync($"/api/contents/{first}", new
        {
            id = first,
            data = new Dictionary<string, object?>
            {
                ["Resource"] = resource.ToString(),
                ["Start"] = Day.AddHours(10).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["End"] = Day.AddHours(11).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Status"] = "cancelled",
            },
        }, Ct);
        cancel.IsSuccessStatusCode.Should().BeTrue(await cancel.Content.ReadAsStringAsync(Ct));

        (await BookAsync(client, resource, 10, 11)).IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task A_booking_can_be_saved_again_without_clashing_with_itself()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());
        var id = await BookedAsync(client, resource, 10, 11);

        var res = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object?>
            {
                ["Resource"] = resource.ToString(),
                ["Start"] = Day.AddHours(10).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["End"] = Day.AddHours(11.5).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["Customer"] = "Ana",
            },
        }, Ct);

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_booking_without_an_offset_or_for_an_unknown_resource_is_refused()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());

        var noOffset = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = TypeName,
            data = new Dictionary<string, object?>
            {
                ["Resource"] = resource.ToString(),
                ["Start"] = "2031-06-02T10:00:00",
                ["End"] = "2031-06-02T11:00:00",
            },
        }, Ct);
        noOffset.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await BookAsync(client, Guid.NewGuid(), 10, 11)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Availability_lists_free_slots_in_the_resource_time_zone_and_drops_a_booked_one()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody(
            zone: "Asia/Manila",
            hours: [new { day = "Monday", start = "09:00", end = "12:00" }]));

        var from = new DateTimeOffset(2031, 6, 2, 0, 0, 0, TimeSpan.FromHours(8));
        var before = await AvailabilityAsync(resource, from, from.AddDays(1));
        var slots = before.GetProperty("slots").EnumerateArray().ToList();

        slots.Should().HaveCount(3);
        slots[0].GetProperty("start").GetString().Should().Be("2031-06-02T09:00:00+08:00");
        before.GetProperty("timeZone").GetString().Should().Be("Asia/Manila");

        // 10:00 local is 02:00Z.
        (await BookAsync(client, resource, 2, 3)).IsSuccessStatusCode.Should().BeTrue();
        var after = (await AvailabilityAsync(resource, from, from.AddDays(1))).GetProperty("slots").EnumerateArray().ToList();

        after.Should().HaveCount(2);
        after.Select(s => s.GetProperty("start").GetString()).Should().NotContain("2031-06-02T10:00:00+08:00");
    }

    [Fact]
    public async Task Availability_honours_a_closed_exception()
    {
        var client = await AdminAsync();
        var open = await CreateResourceAsync(client, ResourceBody(hours: [new { day = "Monday", start = "09:00", end = "12:00" }]));
        var closed = await CreateResourceAsync(client, ResourceBody(
            hours: [new { day = "Monday", start = "09:00", end = "12:00" }],
            exceptions: [new { date = "2031-06-02", closed = true }]));

        (await AvailabilityAsync(open, Day, Day.AddDays(1))).GetProperty("slots").GetArrayLength().Should().Be(3);
        (await AvailabilityAsync(closed, Day, Day.AddDays(1))).GetProperty("slots").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_public_availability_response_holds_no_booking_fields()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());
        await BookedAsync(client, resource, 10, 11);
        (await StoredBookingsAsync(resource)).Should().NotBeEmpty();

        var url = $"/api/public/availability?resource={resource}&from=2031-06-02T00:00:00Z&to=2031-06-03T00:00:00Z";
        var text = await (await _factory.CreateClient().GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);

        var root = JsonDocument.Parse(text).RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["contract", "resource", "timeZone", "slotMinutes", "truncated", "slots"]);
        root.GetProperty("slots").EnumerateArray().Should().NotBeEmpty();
        root.GetProperty("slots").EnumerateArray()
            .Should().OnlyContain(s => s.EnumerateObject().Select(p => p.Name).OrderBy(n => n).SequenceEqual(new[] { "end", "start" }));
        text.Should().NotContain("Customer").And.NotContain("Status").And.NotContain("bookingprobe");
    }

    [Theory]
    [InlineData("resource=not-a-guid&from=2031-06-02T00:00:00Z&to=2031-06-03T00:00:00Z")]
    [InlineData("resource={id}&from=2031-06-02T00:00:00&to=2031-06-03T00:00:00")]
    [InlineData("resource={id}&from=2031-06-03T00:00:00Z&to=2031-06-02T00:00:00Z")]
    [InlineData("resource={id}&from=2031-01-01T00:00:00Z&to=2031-12-31T00:00:00Z")]
    public async Task Availability_refuses_a_bad_or_unbounded_range(string query)
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody());

        var res = await _factory.CreateClient().GetAsync(
            "/api/public/availability?" + query.Replace("{id}", resource.ToString()), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Availability_for_an_unknown_resource_is_not_found()
    {
        var res = await _factory.CreateClient().GetAsync(
            $"/api/public/availability?resource={Guid.NewGuid()}&from=2031-06-02T00:00:00Z&to=2031-06-03T00:00:00Z", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_caller_without_the_capability_cannot_write_resources()
    {
        var admin = await AdminAsync();
        var existing = await CreateResourceAsync(admin, ResourceBody());
        var outsider = await ClientAsync("NoBookingAccess");

        (await outsider.PostAsJsonAsync("/api/booking/resources", ResourceBody(), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.PutAsJsonAsync($"/api/booking/resources/{existing}", ResourceBody("Changed"), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.DeleteAsync($"/api/booking/resources/{existing}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await outsider.GetAsync("/api/booking/resources", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anonymous = _factory.CreateClient();
        (await anonymous.PostAsJsonAsync("/api/booking/resources", ResourceBody(), Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await admin.GetFromJsonAsync<JsonElement>($"/api/booking/resources/{existing}", Ct))
            .GetProperty("name").GetString().Should().Be("Room");
    }

    [Fact]
    public async Task A_resource_with_a_bad_time_zone_or_hours_is_refused()
    {
        var client = await AdminAsync();

        (await client.PostAsJsonAsync("/api/booking/resources", ResourceBody(zone: "Mars/Olympus"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/booking/resources",
            ResourceBody(hours: [new { day = "Monday", start = "17:00", end = "09:00" }]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/booking/resources",
            ResourceBody(hours: [new { day = "Monday", start = "09:00", end = "12:00" }, new { day = "Monday", start = "11:00", end = "13:00" }]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Monday,Tuesday")]
    [InlineData("9")]
    public async Task An_opening_window_day_must_be_one_weekday_name(string day)
    {
        var client = await AdminAsync();

        (await client.PostAsJsonAsync("/api/booking/resources",
            ResourceBody(hours: [new { day, start = "09:00", end = "12:00" }]), Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_exception_with_a_null_window_is_refused_not_a_server_error()
    {
        var client = await AdminAsync();

        var res = await client.PostAsJsonAsync("/api/booking/resources",
            ResourceBody(exceptions: [new { date = "2026-12-25", closed = false, hours = new object?[] { null } }]), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_resource_list_is_paged_and_a_resource_with_bookings_cannot_be_deleted()
    {
        var client = await AdminAsync();
        var resource = await CreateResourceAsync(client, ResourceBody("Paged"));
        await CreateResourceAsync(client, ResourceBody("Paged two"));

        var page = await client.GetFromJsonAsync<JsonElement>("/api/booking/resources?pageSize=1", Ct);
        page.GetProperty("items").GetArrayLength().Should().Be(1);
        page.GetProperty("totalItems").GetInt32().Should().BeGreaterThan(1);

        var booking = await BookedAsync(client, resource, 10, 11);
        (await client.DeleteAsync($"/api/booking/resources/{resource}", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        booking.Should().NotBeEmpty();
    }

    [Fact]
    public void The_module_states_the_contract_its_bodies_carry()
    {
        BookingContract.Version.Should().NotBe(0);
        ((barakoCMS.Modules.IBarakoModule)new BookingModule()).HttpContractVersion.Should().Be(BookingContract.Version);
    }
}
