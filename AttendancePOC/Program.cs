using barakoCMS.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register BarakoCMS services
builder.Services.AddBarakoCMS(builder.Configuration);
builder.Services.AddScoped<barakoCMS.Core.Interfaces.ISensitivityService, AttendancePOC.Services.AttendanceSensitivityService>();

var app = builder.Build();

// Use BarakoCMS middleware
app.UseBarakoCMS();

await AttendancePOC.Seeder.SeedAsync(app);

app.Run();

public partial class Program { }
