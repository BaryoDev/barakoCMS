using barakoCMS.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddBarakoCMS(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseBarakoCMS();

app.MapHealthChecks("/health");

await barakoCMS.Data.DataSeeder.SeedAsync(app);

app.Run();
public partial class Program { }
public partial class Program { }
