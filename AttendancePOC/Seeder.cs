using barakoCMS.Models;
using Marten;
using System.Text.Json;

namespace AttendancePOC;

public class Seeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // 1. Create Roles
        var hrRole = new Role { Id = Guid.NewGuid(), Name = "HR" };
        var superAdminRole = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" };
        
        var existingHr = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "HR");
        if (existingHr == null) session.Store(hrRole);

        var existingSuper = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (existingSuper == null) session.Store(superAdminRole);

        // 2. Create Workflow Definition
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Notify HR on New Attendance",
            TriggerContentType = "AttendanceRecord",
            TriggerEvent = "Created",
            Conditions = new Dictionary<string, string>(), // Always trigger
            Actions = new List<WorkflowAction>
            {
                new WorkflowAction 
                { 
                    Type = "Email", 
                    Parameters = new Dictionary<string, string> 
                    { 
                        { "To", "hr-group@company.com" },
                        { "Subject", "New Attendance Record Submitted" },
                        { "Body", "A new attendance record has been submitted." }
                    } 
                }
            }
        };

        var existingWorkflow = await session.Query<WorkflowDefinition>().FirstOrDefaultAsync(w => w.Name == workflow.Name);
        if (existingWorkflow == null)
        {
            session.Store(workflow);
        }

        await session.SaveChangesAsync();
    }
}
