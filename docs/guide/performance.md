# Performance & Tuning Guide

Optimizing BarakoCMS for high-traffic environments.

## Database Tuning (PostgreSQL + Marten)

### Indexing
Marten automatically creates indexes for queried properties, but for complex queries, you should add manual indexes.
In `MartenRegistry`:
```csharp
public class ContentRegistry : MartenRegistry
{
    public ContentRegistry()
    {
        For<Content>()
            .Index(x => x.Data["category"], x => x.IndexName("mt_doc_content_category"));
    }
}
```

### Connection Pooling
Ensure your `appsettings.json` connection string uses pooling:
```json
"ConnectionStrings": {
  "Main": "Host=localhost;Database=barako;Username=postgres;Password=...;Pooling=true;Minimum Pool Size=10;Maximum Pool Size=100;"
}
```

## Application Caching
BarakoCMS uses in-memory caching for schemas. For clustered deployments, configure Redis:
```csharp
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis");
});
```

## Frontend Optimization
- **Image Optimization:** Use `next/image` for automatic resizing and format conversion.
- **Static Generation:** Use `generateStaticParams` for public content pages where possible to serve static HTML.
