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
                session.Store(role);
                Console.WriteLine($"[DataSeeder] Created role: {role.Name}");
            }
        }

        // Save roles to database before querying for them in next step
        await session.SaveChangesAsync();
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

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var existingAdmin = await session.Query<User>().FirstOrDefaultAsync(u => u.Username == username);

            var adminUser = existingAdmin ?? new User { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };

            adminUser.Username = username;
            adminUser.Email = $"{username}@example.com";
            adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
            adminUser.RoleIds = new List<Guid> { superAdminRole.Id, adminRole.Id };

            session.Store(adminUser);
            Console.WriteLine($"[DataSeeder] {(existingAdmin == null ? "Created" : "Updated")} SuperAdmin user: {username}");
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

    private static async Task SeedAttendanceContentTypeAsync(IDocumentSession session)
    {
        var existing = await session.Query<ContentType>()
            .FirstOrDefaultAsync(ct => ct.Name == "AttendanceRecord");

        if (existing != null)
        {
            Console.WriteLine("[DataSeeder] AttendanceRecord content type already exists");
            return;
        }

        var attendanceType = new ContentType
        {
            Id = Guid.NewGuid(),
            Name = "AttendanceRecord",
            Slug = "attendance-record",
            Fields = new Dictionary<string, string>
            {
                { "FirstName", "string" },
                { "LastName", "string" },
                { "Email", "string" },
                { "BirthDay", "datetime" },
                { "JobDescription", "string" },
                { "Gender", "string" },
                { "SSN", "string" }
            },
            CreatedAt = DateTime.UtcNow
        };

        session.Store(attendanceType);
        Console.WriteLine("[DataSeeder] Created AttendanceRecord content type");
    }

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

    internal static async Task BackfillSearchTextAsync(IDocumentSession session)
    {
        // Grouped rather than ToDictionary. Name is not unique: ContentType/Create/Endpoint.cs:59
        // enforces uniqueness by reading before writing, with no unique index behind it, so two
        // definitions can share a name. ToDictionary throws ArgumentException on the second one,
        // and this runs inside the seeder's catch, so the backfill would silently never happen.
        // First wins, which is what the FirstOrDefault this replaced already did.
        var defMap = (await session.Query<ContentTypeDefinition>().ToListAsync())
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First().Fields
                    .Where(f => f.Sensitivity == SensitivityLevel.Public)
                    .Select(f => f.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var updatedCount = 0;
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
                .ToListAsync();

            if (contents.Count == 0)
                break;

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

            await session.SaveChangesAsync();
        }

        Console.WriteLine(
            $"[DataSeeder] Backfilled SearchText for {updatedCount} content documents.");
    }

}
