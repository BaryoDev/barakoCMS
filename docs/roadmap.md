# Roadmap

This roadmap outlines the future development of BarakoCMS, transitioning from a developer tool to an enterprise-ready headless CMS.

::: tip Current Status
Phase 2 is **Complete**. We are currently in **Phase 2.5 (Hardening)** and **Phase 2.6 (Admin UI)**.
:::

## ✅ Phase 1: Advanced RBAC (Completed)
*   **Duration**: 4 Weeks (Finished 2025-12-11)
*   [x] **Advanced RBAC**: Granular permissions, User Groups, Dynamic Conditions ($CURRENT_USER).
*   [x] **Optimistic Concurrency**: Prevent "Lost Updates" with version checks.
*   [x] **Event Sourcing**: Full audit history and time travel foundation.
*   [x] **16 Integration Tests**: 100% passing.

## ✅ Phase 2: Plugin-Based Workflows (Completed)
*   **Duration**: 4 Weeks (Finished 2025-12-16)
*   [x] **Plugin Architecture**: DI-based action discovery using `IWorkflowAction`.
*   [x] **Built-in Actions**: Email, SMS, Webhook, CreateTask, UpdateField, ConditionalAction.
*   [x] **Enhanced Validation**: JSON schema validation for workflow configurations.
*   [x] **122+ Unit Tests**: Solid foundation for extensibility.

## 🚧 Phase 2.5: Production Hardening (~60% Done)
*   **Focus**: Operations, Infrastructure, and Security.
*   [x] **Kubernetes Ready**: Production manifests, HPA, and Resource limits.
*   [x] **DB Automation**: Auto-migrations and backup sidecars.
*   [x] **Security Hardening**: Rate limiting, security headers, and HTTPS enforcement.
*   [x] **Structured Logging**: Serilog integration with Correlation IDs.
*   [x] **Health Monitoring**: UI dashboard for liveness/readiness probes.
*   [ ] **Prometheus/Grafana**: Metrics and dashboard templates.
*   [ ] **GDPR/SOC2**: Compliance preparation and audit logging.

## 🚧 Phase 2.6: SMB Enablement & Admin UI (~40% Done)
*   **Focus**: Visual tools and simplified deployment.
*   [x] **Admin Dashboard foundation**: Next.js 15, Auth, and Ops pages.
*   [x] **Content Management UI**: CRUD interface for schemas and entries.
*   [x] **Backup Management UI**: One-click snapshots and restores.
*   [x] **Next.js Starter**: Pre-configured `barako-nextjs-starter` repo.
*   [ ] **Dynamic Schema Builder**: Visual editor for content types.
*   [ ] **Visual Workflow Builder**: Drag-and-drop action canvas.
*   [ ] **1-Click Cloud Deploy**: Support for Railway/Render/Vercel.

## 📅 Phase 2.7: AI-Native (MCP)
*   **Focus**: Model Context Protocol (MCP) integration.
*   [ ] **MCP Server**: Allow LLMs (Claude/ChatGPT) to manage content directly.
*   [ ] **Knowledge Graph**: Token-optimized exports for AI context.
*   [ ] **AI Agent RBAC**: Specific permissions for automated agents.

## 📅 Phase 3: Content Versioning
*   **Git-like Versioning**: Branching (Draft/Staging/Prod) for content.
*   **Diff Views**: Visual comparison between versions.
*   **Review Flow**: Approval chains and comment threads.

## 📅 Phase 4: Multi-Tenancy
*   **Tenant Isolation**: Logical separation of data per tenant.
*   **SaaS Dashboard**: Analytics and quota management.

## 📅 Phase 5: High Performance
*   **Distributed Caching**: Redis integration.
*   **Global Scale**: CDN support and query optimization.
