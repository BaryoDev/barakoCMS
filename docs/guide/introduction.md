# Introduction to BarakoCMS

BarakoCMS is an **AI-Native, High-Performance Headless CMS** built for .NET 8. It is designed for speed, infinite extensibility, and enterprise-grade robustness.

## Why BarakoCMS?

- **⚡ Unmatched Speed**: Built on [FastEndpoints](https://fast-endpoints.com/) and [MartenDB](https://martendb.io/) for minimal overhead and maximum throughput.
- **🧩 Infinite Extensibility**: Features a plugin architecture, custom content types without schema migrations, and an event-driven workflow engine.
- **🛡️ Enterprise Robustness**: Includes advanced RBAC, event sourcing, idempotency, optimistic concurrency, and sensitive data masking out of the box.

## Technology Stack

- **Core**: .NET 8
- **Database**: PostgreSQL (via MartenDB document store + event sourcing)
- **API Framework**: FastEndpoints (Vertical Slice Architecture)
- **Validation**: FluentValidation
- **Auth**: JWT + BCrypt
