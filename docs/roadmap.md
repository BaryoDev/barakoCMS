# Roadmap

This roadmap outlines the future development of BarakoCMS.

::: tip Current Status
We are currently in **Phase 2**.
:::

## ✅ Phase 1: Stabilization & Core Features (Completed)
*   [x] **Advanced RBAC**: Granular permissions, User Groups, Dynamic Conditions ($CURRENT_USER).
*   [x] **Optimistic Concurrency**: Prevent "Lost Updates" with version checks.
*   [x] **Event Sourcing**: Full audit history and time travel foundation.
*   [x] **Async Workflows**: Non-blocking event processing.

*   [x] `WebhookAction` plugin for HTTP POST webhooks
*   [x] 8 comprehensive plugin architecture tests

### 🚧 In Progress (Week 2+)
*   [ ] Additional action plugins (Webhook, Slack, Discord)
*   [ ] Plugin auto-discovery system
*   [ ] Visual workflow builder UI
*   [ ] Comprehensive plugin developer documentation

## 📅 Phase 3: Content Versioning & Collaboration
*   **Git-like Versioning**: Branching (Draft/Staging/Prod) for content.
*   **Diff Views**: See exactly what changed between versions.
*   **Collaboration**: Multi-user editing presence and locking.
*   **Review Flow**: Approval chains for publishing.

## 📅 Phase 4: Multi-Tenancy & SaaS Ready
*   **Tenant Isolation**: Logical separation of data per tenant.
*   **API Rate Limiting**: Per-tenant quotas.
*   **SaaS Dashboard**: Tenant management and analytics.

## 📅 Phase 5: High Performance & Scale
*   **Distributed Caching**: Redis integration.
*   **CDN Support**: Automatic media offloading.
*   **Query Optimization**: Advanced MartenDB projections.
