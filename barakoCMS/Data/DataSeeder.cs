using Marten;
using barakoCMS.Models;

namespace barakoCMS.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        Console.WriteLine("[DataSeeder] Starting comprehensive data seeding...");

        // 1. Seed Roles (including HR role for AttendancePOC)
        await SeedRolesAsync(session);

        // 2. Seed Users (Admin, HR, Standard users)
        await SeedUsersAsync(session, configuration);

        // 3. Seed AttendancePOC Content Type
        await SeedAttendanceContentTypeAsync(session);

        // 4. Seed AttendancePOC Workflow (Email confirmation)
        await SeedAttendanceWorkflowAsync(session);

        // 5. Seed Sample Attendance Records
        await SeedAttendanceRecordsAsync(session);

        await session.SaveChangesAsync();
        Console.WriteLine("[DataSeeder] ✅ Seeding complete!");
    }

    private static async Task SeedRolesAsync(IDocumentSession session)
    {
        var roles = new[]
        {
            new Role { Id = Guid.NewGuid(), Name = "SuperAdmin", Description = "Full system access" },
            new Role { Id = Guid.NewGuid(), Name = "Admin", Description = "Administrator with full access" },
            new Role { Id = Guid.NewGuid(), Name = "HR", Description = "Human Resources - manage attendance" },
            new Role { Id = Guid.NewGuid(), Name = "User", Description = "Standard user" }
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
    }

    private static async Task SeedUsersAsync(IDocumentSession session, IConfiguration configuration)
    {
        var userCount = await session.Query<User>().CountAsync();
        if (userCount > 0)
        {
            Console.WriteLine("[DataSeeder] Users already exist, skipping user seeding");
            return;
        }

        var superAdminRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        var adminRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "Admin");
        var hrRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "HR");
        var userRole = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "User");

        // Create configured admin
        var adminConfig = configuration.GetSection("InitialAdmin");
        var username = adminConfig["Username"];
        var password = adminConfig["Password"];

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            var adminUser = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = $"{username}@company.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                RoleIds = new List<Guid> { superAdminRole!.Id, adminRole!.Id },
                CreatedAt = DateTime.UtcNow
            };
            session.Store(adminUser);
            Console.WriteLine($"[DataSeeder] Created SuperAdmin user: {username}");
        }

        // Create sample HR user
        var hrUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "hr_manager",
            Email = "hr@company.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("HRPassword123!"),
            RoleIds = new List<Guid> { hrRole!.Id, adminRole!.Id },
            CreatedAt = DateTime.UtcNow
        };
        session.Store(hrUser);
        Console.WriteLine("[DataSeeder] Created HR user: hr_manager");

        // Create sample standard user
        var standardUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "john_viewer",
            Email = "john@company.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("UserPassword123!"),
            RoleIds = new List<Guid> { userRole!.Id },
            CreatedAt = DateTime.UtcNow
        };
        session.Store(standardUser);
        Console.WriteLine("[DataSeeder] Created Standard user: john_viewer");
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

        var sampleRecords = new[]
        {
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "AttendanceRecord",
                Data = new Dictionary<string, object>
                {
                    { "FirstName", "Sarah" },
                    { "LastName", "Johnson" },
                    { "Email", "sarah.johnson@company.com" },
                    { "BirthDay", "1990-05-15" },
                    { "JobDescription", "Software Engineer" },
                    { "Gender", "Female" },
                    { "SSN", "123-45-6789" }
                },
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Sensitive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "AttendanceRecord",
                Data = new Dictionary<string, object>
                {
                    { "FirstName", "Michael" },
                    { "LastName", "Chen" },
                    { "Email", "michael.chen@company.com" },
                    { "BirthDay", "1985-11-23" },
                    { "JobDescription", "Product Manager" },
                    { "Gender", "Male" },
                    { "SSN", "987-65-4321" }
                },
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Sensitive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "AttendanceRecord",
                Data = new Dictionary<string, object>
                {
                    { "FirstName", "Emily" },
                    { "LastName", "Rodriguez" },
                    { "Email", "emily.rodriguez@company.com" },
                    { "BirthDay", "1992-03-08" },
                    { "JobDescription", "UX Designer" },
                    { "Gender", "Female" },
                    { "SSN", "456-78-9012" }
                },
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Sensitive,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        foreach (var record in sampleRecords)
        {
            session.Store(record);
            Console.WriteLine($"[DataSeeder] Created attendance record: {record.Data["FirstName"]} {record.Data["LastName"]}");
        }
    }
}
