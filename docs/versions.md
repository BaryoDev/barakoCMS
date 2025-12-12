# Version History

## Current Version: **2.0.2** ✨

---

## v2.0.2 (2025-12-12)

### 🎯 Plugin-Based Workflow System - Phase 2 Complete

**New Features:**
- **6 Extensible Workflow Actions**:
  - `EmailAction` - Send email notifications
  - `SmsAction` - Send SMS messages
  - `WebhookAction` - HTTP POST to external webhooks
  - `CreateTaskAction` - Auto-generate content items
  - `UpdateFieldAction` - Modify content fields dynamically
  - `ConditionalAction` - If/then/else workflow logic

**Testing:**
- 132 total tests (19 new plugin tests)
- 100% pass rate
- Comprehensive plugin architecture coverage

**Documentation:**
- Complete plugin development guide
- 7 real-world workflow examples
- API reference for all 6 actions

**Breaking Changes:** None - all additive

---

## v2.0.1 (2025-12-11)

### 📚 Documentation & Deployment

- VitePress documentation site deployed
- Complete API reference
- Ko-Fi support integration
- Enhanced roadmap visibility

---

## v2.0.0 (2025-12-10)

### 🔐 Advanced RBAC System

**Features:**
- Granular permission system
- User groups and role hierarchies
- Dynamic permission conditions
- Multi-tenancy support
- Event sourcing foundation
- Optimistic concurrency control

**Infrastructure:**
- FastEndpoints integration
- Marten (PostgreSQL) document store
- Health checks and resilience patterns

---

## v1.x - Legacy

Legacy versions focused on basic headless CMS functionality.

---

## Upgrading

### From 2.0.1 to 2.0.2

**No breaking changes** - simply update your package reference:

```xml
<PackageReference Include="BarakoCMS" Version="2.0.2" />
```

**New Capabilities:**
1. Register workflow action plugins in your `Program.cs`
2. Create workflow definitions via API
3. Extend with custom `IWorkflowAction` implementations

See the [Workflow Plugins Guide](/workflows/plugins) for details.

### From 2.0.0 to 2.0.1

No code changes required - documentation and metadata updates only.

---

## Support

- **Documentation**: [https://baryodev.github.io/barakoCMS](https://baryodev.github.io/barakoCMS)
- **GitHub**: [https://github.com/baryodev/barakoCMS](https://github.com/baryodev/barakoCMS)
- **NuGet**: [https://www.nuget.org/packages/BarakoCMS](https://www.nuget.org/packages/BarakoCMS)
