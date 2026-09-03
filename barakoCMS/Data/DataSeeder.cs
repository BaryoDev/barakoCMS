using Marten;
using Microsoft.Extensions.Hosting;
using barakoCMS.Models;

namespace barakoCMS.Data;

// Stays public: BarakoCMS.Suite's host calls it at startup, and so would anyone assembling their
// own host who wants the canonical roles and the initial admin seeded. That makes it contract
// rather than host detail.
public static class DataSeeder
{
    private const int batchSize = 1000;
    public static async Task SeedAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        Console.WriteLine("[DataSeeder] Starting comprehensive data seeding...");

        // 1. Seed Roles (including HR role for attendance demo)
        await SeedRolesAsync(session);

        // 2. Seed Users (Admin, HR, Standard users)
        await SeedUsersAsync(session, configuration);

        // 3-5. Everything demo. One gate at the top so the next demo seeder added below inherits it
        // instead of having to remember. See SeedsDemoContent for what decides this.
        if (SeedsDemoContent(configuration, environment))
        {
            await SeedAttendanceContentTypeAsync(session);
            await SeedAttendanceWorkflowAsync(session);
            await SeedAttendanceRecordsAsync(session);
        }
        else
        {
            Console.WriteLine("[DataSeeder] Demo content skipped (Seed:DemoContent is off)");
        }

        // Committed before the backfill, which queries for content that needs indexing and would
        // otherwise not see anything seeded in this run until the next boot.
        await session.SaveChangesAsync();

        // 6. Backfill SearchText for existing content
        await BackfillSearchTextAsync(session);

        await session.SaveChangesAsync();
        Console.WriteLine("[DataSeeder] ✅ Seeding complete!");
    }

    /// <summary>
    /// Whether this host seeds the demo AttendanceRecord content type, its sample records and the
    /// "Attendance Confirmation Email" workflow.
    /// </summary>
    /// <remarks>
    /// These used to be unconditional, so every production first run came up with an attendance
    /// schema nobody asked for and a stored-active workflow that mails whatever address
    /// <c>{{data.Email}}</c> holds. Once an operator configures Resend, a demo fixture becomes an
    /// outbound mail path in their system.
    ///
    /// Setting <c>Seed:DemoContent</c> (env <c>Seed__DemoContent</c>) decides it. Unset, it follows
    /// the environment: on in Development, off everywhere else. That default is what makes the
    /// quickstart, which runs as Production, safe without anyone reading this file. A developer who
    /// wants the sample content there sets the variable. See issue #283.
    /// </remarks>
    internal static bool SeedsDemoContent(IConfiguration configuration, IHostEnvironment environment)
    {
        // The host's own answer, not ASPNETCORE_ENVIRONMENT read back directly. The two disagree:
        // a host started with only DOTNET_ENVIRONMENT=Development is in Development and that
        // variable is empty, so reading it would refuse the demo content in exactly the environment
        // that wants it. Taking it as a parameter also keeps the decision testable without any test
        // reaching for a process-wide variable that every other test can see.
        return configuration.GetValue("Seed:DemoContent", environment.IsDevelopment());
    }

    // Well-known deterministic GUIDs for system roles (must match CachedPermissionResolver)
    // Kept as aliases so existing callers and tests compile. The values live in
    // Models.SystemRoles, which is also what the API reports IsSystem from.
    public static readonly Guid SuperAdminRoleId = barakoCMS.Models.SystemRoles.SuperAdminRoleId;
    public static readonly Guid AdminRoleId = barakoCMS.Models.SystemRoles.AdminRoleId;
    public static readonly Guid HRRoleId = barakoCMS.Models.SystemRoles.HRRoleId;
    public static readonly Guid UserRoleId = barakoCMS.Models.SystemRoles.UserRoleId;

    private static async Task SeedRolesAsync(IDocumentSession session)
    {
        // Use deterministic GUIDs for system roles to enable SuperAdmin bypass in CachedPermissionResolver
        var roles = new[]
        {
            new Role { Id = SuperAdminRoleId, Name = "SuperAdmin", Description = "Full system access" },
            new Role { Id = AdminRoleId, Name = "Admin", Description = "Administrator with full access" },
            new Role { Id = HRRoleId, Name = "HR", Description = "Human Resources - manage attendance" },
            new Role { Id = UserRoleId, Name = "User", Description = "Standard user" }
        };

        foreach (var role in roles)
        {
            var existing = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == role.Name);
            if (existing == null)
            {
                ApplyCapabilityDefaults(role);
                session.Store(role);
                Console.WriteLine($"[DataSeeder] Created role: {role.Name}");
            }
            else if (ApplyCapabilityDefaults(existing))
            {
                session.Store(existing);
                Console.WriteLine($"[DataSeeder] Backfilled system capabilities on role: {existing.Name}");
            }
        }

        // Save roles to database before querying for them in next step
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Gives a seeded system role the capabilities matching what it could already reach, and reports
    /// whether that changed anything.
    /// </summary>
    /// <remarks>
    /// This is the upgrade path, and it runs on every seed rather than only on an empty list.
    ///
    /// Filling only an empty list was the original rule, and it stopped working the moment the
    /// vocabulary started growing. A deployment upgraded once has an Admin carrying the names that
    /// existed then, so the count is not zero and every capability added afterwards never arrives.
    /// Nothing breaks while <c>Auth:LegacyRoleFallback</c> is on, because the gate still honours the
    /// role names it replaced. Turn the fallback off, which is the whole point of the migration, and
    /// that Admin silently loses every area migrated after its own upgrade.
    ///
    /// So the defaults are unioned in, and the cost is stated rather than hidden: a default an
    /// operator has deliberately removed from a seeded role comes back on the next restart. Removing
    /// one for good means editing the role to something other than the default set, or not running
    /// the seeder. A seeded system role is core's to define; a role of your own is untouched, because
    /// <see cref="barakoCMS.Models.SystemCapabilities.DefaultsFor"/> returns nothing for a name the
    /// seeder does not create.
    ///
    /// Access does not depend on this having run. This is what makes the capabilities visible and
    /// editable, not what keeps the lights on.
    /// </remarks>
    internal static bool ApplyCapabilityDefaults(Role role)
    {
        var defaults = barakoCMS.Models.SystemCapabilities.DefaultsFor(role.Name);
        if (defaults.Count == 0)
            return false;

        var missing = defaults
            .Where(d => !role.SystemCapabilities.Contains(d, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
            return false;

        role.SystemCapabilities = [.. role.SystemCapabilities, .. missing];
        return true;
    }

    /// <summary>
    /// A random initial-admin password, built to satisfy
    /// <see cref="barakoCMS.Infrastructure.Services.PasswordPolicyValidator"/> so the account it is
    /// set on can also change it later.
    /// </summary>
    /// <remarks>
    /// One character is taken from each required class first and the rest from the union, then the
    /// whole thing is shuffled. Sampling the union alone would satisfy the policy almost always
    /// rather than always, and "almost" here means a first boot that seeds an account whose password
    /// the change-password endpoint then refuses.
    ///
    /// <see cref="System.Security.Cryptography.RandomNumberGenerator.GetItems{T}"/> is uniform and
    /// rejection-samples, so there is no modulo bias to reason about.
    /// </remarks>
    internal static string GenerateInitialPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*_+=?";

        var all = (upper + lower + digits + symbols).AsSpan();

        Span<char> buffer = stackalloc char[24];
        buffer[0] = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(upper.AsSpan(), 1)[0];
        buffer[1] = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(lower.AsSpan(), 1)[0];
        buffer[2] = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(digits.AsSpan(), 1)[0];
        buffer[3] = System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(symbols.AsSpan(), 1)[0];
        System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(all, buffer[4..]);
        System.Security.Cryptography.RandomNumberGenerator.Shuffle(buffer);

        return new string(buffer);
    }

    private static async Task SeedUsersAsync(IDocumentSession session, IConfiguration configuration)
    {
        var userCount = await session.Query<User>().CountAsync();
        if (userCount > 0)
        {
            Console.WriteLine("[DataSeeder] Users already exist, skipping user seeding");
            return;
        }

        // Use the well-known role IDs directly instead of querying
        // This avoids potential race conditions and ensures consistency
        var superAdminRole = new Role { Id = SuperAdminRoleId, Name = "SuperAdmin" };
        var adminRole = new Role { Id = AdminRoleId, Name = "Admin" };
        var hrRole = new Role { Id = HRRoleId, Name = "HR" };
        var userRole = new Role { Id = UserRoleId, Name = "User" };

        // Roles are now using deterministic IDs, no validation needed

        // Create configured admin
        var adminConfig = configuration.GetSection("InitialAdmin");
        var username = adminConfig["Username"];
        var password = adminConfig["Password"];

        if (!string.IsNullOrEmpty(username))
        {
            // No password configured means one gets generated and printed here, once, instead of
            // the account not being created. The compose files used to default it to a literal
            // ("changeme-in-production"), so a stack brought up with no .env had a SuperAdmin whose
            // password was published in this repository. Refusing to seed instead would leave a
            // first-run stack with no way in at all. See issue #271.
            var generated = string.IsNullOrEmpty(password);
            if (generated)
            {
                password = GenerateInitialPassword();
            }

            var existingAdmin = await session.Query<User>().FirstOrDefaultAsync(u => u.Username == username);

            var adminUser = existingAdmin ?? new User { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };

            adminUser.Username = username;
            adminUser.Email = $"{username}@example.com";
            adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            adminUser.RoleIds = new List<Guid> { superAdminRole.Id, adminRole.Id };

            session.Store(adminUser);
            Console.WriteLine($"[DataSeeder] {(existingAdmin == null ? "Created" : "Updated")} SuperAdmin user: {username}");

            if (generated)
            {
                // The one place this codebase prints a credential, and it is deliberate: a password
                // nobody can read is the same as no account. It goes to the console only, never to
                // the logger, so it does not reach a log sink or an aggregator.
                Console.WriteLine(
                    $"[DataSeeder] No InitialAdmin:Password was set, so one was generated for '{username}': {password}");
                Console.WriteLine(
                    "[DataSeeder] This is printed once and is not recoverable. Sign in, change it, then set InitialAdmin:Password.");
            }
        }

        // Sample login accounts with fixed passwords are demo data — only seed them outside
        // production so they never ship as usable accounts in a real deployment.
        var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";

        if (userCount == 0 && isDevelopment)
        {
            // Create sample HR user
            var hrUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "hr_manager",
                Email = "hr@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("HRPassword123!"),
                RoleIds = new List<Guid> { hrRole.Id, adminRole.Id },
                CreatedAt = DateTime.UtcNow
            };
            session.Store(hrUser);
            Console.WriteLine("[DataSeeder] Created HR user: hr_manager");

            // Create sample standard user
            var standardUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "john_viewer",
                Email = "john@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("UserPassword123!"),
                RoleIds = new List<Guid> { userRole.Id },
                CreatedAt = DateTime.UtcNow
            };
            session.Store(standardUser);
            Console.WriteLine("[DataSeeder] Created Standard user: john_viewer");
        }
    }

    /// <summary>
    /// The demo content type. Fixed rather than generated so a re-seed upserts the same row instead
    /// of racing a second one in beside it.
    /// </summary>
    internal static readonly Guid AttendanceContentTypeId = new("6f3b1c9e-2a44-4d1e-9a1c-2f0d5b8e7c11");

    /// <summary>
    /// Seeds the demo content type as a <see cref="ContentTypeDefinition"/>, the type the API and the
    /// admin both read.
    /// </summary>
    /// <remarks>
    /// This used to write a <c>Models.ContentType</c>, a different class in a different table that
    /// nothing outside this file read. A freshly seeded instance therefore logged that it had created
    /// a content type while <c>GET /api/content-types</c> returned nothing and the schema editor was
    /// empty, and the demo entries validated against no schema at all, because a missing definition
    /// means loose mode. See issue #322.
    /// </remarks>
    internal static async Task SeedAttendanceContentTypeAsync(IDocumentSession session)
    {
        var existing = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == AttendanceContentTypeName);

        if (existing != null)
        {
            Console.WriteLine("[DataSeeder] AttendanceRecord content type already exists");
            return;
        }

        session.Store(AttendanceContentType());

        // Committed here rather than at the end of the seed run: the records seeded next are
        // validated and search-indexed against this definition, and a query in the same session does
        // not see an uncommitted store.
        await session.SaveChangesAsync();
        Console.WriteLine("[DataSeeder] Created AttendanceRecord content type");
    }

    internal const string AttendanceContentTypeName = "AttendanceRecord";

    /// <summary>The demo schema. SSN is Sensitive, so it is masked on read and stays out of SearchText.</summary>
    internal static ContentTypeDefinition AttendanceContentType() => new()
    {
        Id = AttendanceContentTypeId,
        Name = AttendanceContentTypeName,
        DisplayName = "Attendance Record",
        Description = "Demo content type seeded on a fresh install.",
        Fields = new List<FieldDefinition>
        {
            new() { Name = "FirstName", DisplayName = "First Name", Type = "string", IsRequired = true },
            new() { Name = "LastName", DisplayName = "Last Name", Type = "string", IsRequired = true },
            new() { Name = "Email", DisplayName = "Email", Type = "email" },
            new() { Name = "BirthDay", DisplayName = "Birth Day", Type = "date" },
            new() { Name = "JobDescription", DisplayName = "Job Description", Type = "string" },
            new() { Name = "Gender", DisplayName = "Gender", Type = "string" },
            new()
            {
                Name = "SSN",
                DisplayName = "SSN",
                Type = "string",
                Sensitivity = SensitivityLevel.Sensitive,
                Mask = FieldMask.Last4,
            },
        },
    };

    private static async Task SeedAttendanceWorkflowAsync(IDocumentSession session)
    {
        var existing = await session.Query<WorkflowDefinition>()
            .FirstOrDefaultAsync(w => w.Name == "Attendance Confirmation Email");

        if (existing != null)
        {
            Console.WriteLine("[DataSeeder] Attendance workflow already exists");
            return;
        }

        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Attendance Confirmation Email",
            TriggerContentType = "AttendanceRecord",
            TriggerEvent = "Created",
            Conditions = new Dictionary<string, string>
            {
                { "status", "Published" }
            },
            Actions = new List<WorkflowAction>
            {
                new WorkflowAction
                {
                    Type = "SendEmail",
                    Parameters = new Dictionary<string, string>
                    {
                        { "To", "{{data.Email}}" },
                        { "Subject", "Attendance Record Created - {{data.FirstName}} {{data.LastName}}" },
                        { "Body", "Hello {{data.FirstName}},\n\nYour attendance record has been successfully created.\n\nThank you!" }
                    }
                }
            }
        };

        session.Store(workflow);
        Console.WriteLine("[DataSeeder] Created Attendance Confirmation Email workflow");
    }

    private static async Task SeedAttendanceRecordsAsync(IDocumentSession session)
    {
        var recordCount = await session.Query<Content>()
            .Where(c => c.ContentType == "AttendanceRecord")
            .CountAsync();

        if (recordCount > 0)
        {
            Console.WriteLine("[DataSeeder] Attendance records already exist");
            return;
        }

        foreach (var record in SampleAttendanceRecords())
        {
            session.Store(record);
            Console.WriteLine($"[DataSeeder] Created attendance record: {record.Data["FirstName"]} {record.Data["LastName"]}");
        }
    }

    /// <summary>
    /// The demo AttendanceRecord rows a fresh install starts with.
    /// </summary>
    /// <remarks>
    /// Every value here is deliberately unusable as personal data. The seed is the first content
    /// anyone sees, so it is also the worked example of how to fill a content type, and it used to
    /// carry three well-formed Social Security numbers, names that read as real people, and mail
    /// addresses at a registered domain. Scanners flag an SSN-shaped string wherever they find it,
    /// and a shape that reads as real is the shape people copy. So: no digit group that matches an
    /// SSN, and mail only at example.com, which RFC 2606 reserves for documentation.
    ///
    /// Exposed for the test that asserts the class of data stays out. See issue #265.
    /// </remarks>
    internal static IReadOnlyList<Content> SampleAttendanceRecords() =>
    [
        SampleAttendanceRecord("Sample", "Employee One", "SAMPLE-NOT-A-REAL-SSN-1", "Software Engineer"),
        SampleAttendanceRecord("Sample", "Employee Two", "SAMPLE-NOT-A-REAL-SSN-2", "Product Manager"),
        SampleAttendanceRecord("Sample", "Employee Three", "SAMPLE-NOT-A-REAL-SSN-3", "UX Designer")
    ];

    private static Content SampleAttendanceRecord(string firstName, string lastName, string ssn, string jobDescription) =>
        new()
        {
            Id = Guid.NewGuid(),
            ContentType = "AttendanceRecord",
            Data = new Dictionary<string, object>
            {
                { "FirstName", firstName },
                { "LastName", lastName },
                { "Email", $"{lastName.Replace(" ", "-").ToLowerInvariant()}@example.com" },
                { "BirthDay", "2000-01-01" },
                { "JobDescription", jobDescription },
                { "Gender", "Unspecified" },
                { "SSN", ssn }
            },
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Sensitive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    /// <summary>
    /// Fills SearchText for content that predates it, one batch at a time.
    /// </summary>
    /// <remarks>
    /// The seeder runs in an un-awaited Task.Run whose catch only logs, so a backfill that runs out
    /// of memory or time on a large corpus leaves the application serving traffic with public search
    /// returning nothing for pre-existing content, indefinitely. That is why a run that does not
    /// reach the end says so, and says how far it got, before rethrowing: a partial run and a
    /// completed one used to leave identical evidence. See issue #167.
    /// </remarks>
    internal static async Task BackfillSearchTextAsync(
        IDocumentSession session, CancellationToken ct = default)
    {
        var updatedCount = 0;
        var scannedCount = 0;
        var batchNumber = 0;

        try
        {
            // Grouped rather than ToDictionary. Name is unique per tenant from 4.0 on, but the index
            // that enforces it is not applied to an existing database under AutoCreate.CreateOnly, so
            // a store seeded before then can still hold two definitions sharing a name. ToDictionary
            // throws ArgumentException on the second one, and that throw used to be swallowed by the
            // seeder's catch, so the backfill would silently never happen. First wins, which is what
            // the FirstOrDefault this replaced already did.
            var defMap = (await session.Query<ContentTypeDefinition>().ToListAsync(ct))
                .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.First().Fields
                        .Where(f => f.Sensitivity == SensitivityLevel.Public)
                        .Select(f => f.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            Guid? lastId = null;

            while (true)
            {
                var query = session.Query<Content>()
                    .Where(c => c.SearchText == null);

                if (lastId.HasValue)
                    query = query.Where(c => c.Id > lastId.Value);

                var contents = await query
                    .OrderBy(c => c.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (contents.Count == 0)
                    break;

                batchNumber++;
                scannedCount += contents.Count;

                foreach (var content in contents)
                {
                    if (!defMap.TryGetValue(content.ContentType, out var publicFields))
                        continue;

                    content.SearchText = string.Join(
                        ' ',
                        content.Data
                            .Where(kv => publicFields.Contains(kv.Key))
                            .Select(kv => kv.Value?.ToString())
                            .Where(v => !string.IsNullOrWhiteSpace(v)));

                    session.Store(content);
                    updatedCount++;
                }

                lastId = contents[^1].Id;

                await session.SaveChangesAsync(ct);

                // Per batch, not once at the end. A run that dies partway leaves the log as the only
                // record of how far it got.
                Console.WriteLine(
                    $"[DataSeeder] SearchText backfill batch {batchNumber}: {scannedCount} scanned, "
                    + $"{updatedCount} updated so far.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[DataSeeder] SearchText backfill DID NOT COMPLETE after {batchNumber} batch(es), "
                + $"{scannedCount} scanned, {updatedCount} updated: {ex.GetType().Name}: {ex.Message}. "
                + "Public search will return nothing for content that was not reached. Rerun the seeder.");
            throw;
        }

        Console.WriteLine(
            $"[DataSeeder] Backfilled SearchText for {updatedCount} content documents. "
            + $"Completed: {scannedCount} scanned in {batchNumber} batch(es).");
    }

}
