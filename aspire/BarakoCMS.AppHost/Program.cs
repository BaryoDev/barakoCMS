// The barako stack under Aspire: the API as a project, Postgres and SeaweedFS as containers, and
// barakoBrew and barakoPress from their published images, wired by service discovery instead of
// hand-set addresses. A spike (#1025); docs/internal/aspire-spike.md has what it showed.
//
// Every secret is a generated parameter, so nothing here is a literal and nothing has to be set
// before the first run. The dashboard shows the generated admin password under Parameters, and
// `dotnet user-secrets` can pin any of them.

var builder = DistributedApplication.CreateBuilder(args);

var dbPassword = builder.AddParameter("db-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true);
var jwtKey = builder.AddParameter("jwt-key", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true);
var adminPassword = builder.AddParameter("admin-password", new GenerateParameterDefault { MinLength = 16 }, secret: true);

// The images of the two clients live in their own repositories, so they are configuration
// (Images:Brew, Images:Press) rather than names this file claims to know.
var brewImage = builder.Configuration["Images:Brew"] ?? "ghcr.io/baryodev/barakobrew";
var pressImage = builder.Configuration["Images:Press"] ?? "ghcr.io/baryodev/barakopress";
var clientTag = builder.Configuration["Images:Tag"] ?? "latest";

var postgres = builder.AddPostgres("postgres", password: dbPassword)
    .WithImageTag("16-alpine")
    .WithContainerName("barako-aspire-postgres")
    .WithDataVolume("barako-aspire-pgdata");
var database = postgres.AddDatabase("DefaultConnection", databaseName: "barakocms");

var seaweed = builder.AddContainer("seaweedfs", "chrislusf/seaweedfs", "4.46")
    .WithContainerName("barako-aspire-seaweedfs")
    .WithArgs("server", "-s3", "-dir=/data")
    .WithVolume("barako-aspire-seaweed", "/data")
    .WithHttpEndpoint(targetPort: 8333, name: "s3");

// The Suite has no launchSettings.json (CLAUDE.md section 3 keeps them out), so the endpoint is
// declared here; Aspire hands it to the process through ASPNETCORE_URLS.
var api = builder.AddProject("api", "../../BarakoCMS.Suite/BarakoCMS.Suite.csproj", launchProfileName: null)
    .WithHttpEndpoint(name: "http")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(seaweed)
    .WithEnvironment("JWT__Key", jwtKey)
    .WithEnvironment("InitialAdmin__Username", "admin")
    .WithEnvironment("InitialAdmin__Password", adminPassword)
    .WithEnvironment("Kubernetes__Enabled", "false")
    .WithEnvironment("Modules__Files.S3__Bucket", "barako")
    .WithEnvironment("Modules__Files.S3__ServiceUrl", seaweed.GetEndpoint("s3"))
    .WithHttpHealthCheck("/health");

builder.AddContainer("brew", brewImage, clientTag)
    .WithContainerName("barako-aspire-brew")
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WithEnvironment("CMS_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.AddContainer("press", pressImage, clientTag)
    .WithContainerName("barako-aspire-press")
    .WithHttpEndpoint(targetPort: 3000, name: "http")
    .WithEnvironment("NEXT_PUBLIC_API_URL", api.GetEndpoint("http"))
    .WithEnvironment("CMS_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.AddDockerComposeEnvironment("compose");

builder.Build().Run();
