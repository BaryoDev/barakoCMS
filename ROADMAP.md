# 🗺️ BarakoCMS Roadmap

This document outlines the strategic direction and development milestones for BarakoCMS.

## ✅ Completed Milestones

### v1.0.0 - Core Foundation (Released)
- [x] **FastEndpoints Integration**: High-performance API endpoints.
- [x] **MartenDB Storage**: Document storage with PostgreSQL.
- [x] **Basic CRUD**: Create, Read, Update, Delete for Content and ContentTypes.
- [x] **Authentication**: JWT-based auth with User/Role management.

### v1.1.0 - Robustness & Validation (Released: 2025-12-05)
- [x] **Runtime Validation**: Schema enforcement for field types and names.
- [x] **Strict Mode**: Configurable validation via `appsettings.json`.
- [x] **Test Suite**: 100% Unit & Integration test coverage.
- [x] **Process Documentation**: `RELEASE_PROCESS.md` and `DEVELOPMENT_STANDARDS.md`.

---

## 🚧 Current Focus: v1.2.0 - Workflow Engine

**Goal**: Enable event-driven business logic and state transitions.

- [ ] **State Machine**: Define valid status transitions (e.g., Draft -> Review -> Published).
- [ ] **Event Triggers**: Execute actions on events (e.g., `ContentCreated`, `ContentUpdated`).
- [ ] **Action Plugins**:
    - [ ] Email Notification (SendGrid/SMTP).
    - [ ] Webhooks (Slack/Discord integration).
- [ ] **Workflow Designer**: JSON-based workflow definitions.

---

## 🔮 Future Horizons

### v1.3.0 - Plugin Architecture
- [ ] **IService Interface**: Standardized interfaces for core services.
- [ ] **Dynamic Loading**: Load DLLs/Assemblies at runtime.
- [ ] **Marketplace Prep**: Structure for distributing community plugins.

### v1.4.0 - AI Agent Integration
- [ ] **RAG Pipeline**: Built-in vector search for content.
- [ ] **Agent API**: Specialized endpoints for AI agents to query/mutate content efficiently.
- [ ] **Auto-Tagging**: AI-powered content classification.

### v2.0.0 - The "Barako" UI
- [ ] **Headless-First UI**: A reference Admin Dashboard built with React/Next.js.
- [ ] **Visual Editor**: Drag-and-drop content modeling.

---

## 📉 Backlog / Ideas
- [ ] **Multi-Tenancy**: Support multiple sites/tenants in a single instance.
- [ ] **GraphQL Support**: Alternative to REST API.
- [ ] **S3 Storage**: Offload media assets to S3-compatible storage.
