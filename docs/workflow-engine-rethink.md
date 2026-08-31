# Workflow engine: rethink and improvement plan

**Status:** Parked (design notes for a future pickup)
**Scope:** `barakoCMS/Features/Workflows/*`, `barakoCMS/Models/WorkflowDefinition.cs`, `WorkflowProjection`, admin workflow UI
**TL;DR:** The engine's basic shape (content-event → conditions → actions, async, pluggable actions) is sound. The work is in **trust** (idempotency, run history, retries), **consistency** (one expression language, fix trigger gaps), and **expressiveness** (structured data, cross-action outputs). Harden and expand the rules engine; don't over-build a durable orchestrator yet.

---

## 1. What it is today

A **content-event → conditions → flat action list** rules engine, explicitly "not a state machine" (`admin/src/types/workflow.ts:2`).

- **Trigger:** a Marten `EventProjection` (`WorkflowProjection.cs:13`) running **async** under the daemon in Solo mode (`Extensions/ServiceCollectionExtensions.cs:252,261`). Producing endpoints do **not** await workflow execution (`Features/Content/Update/Endpoint.cs:113-114`).
  - `ContentUpdated`→`"Updated"`, `ContentCreated`→`"Created"`, `ContentStatusChanged`→`"Published"` **only when NewStatus == Published** (`WorkflowProjection.cs:36-39`).
- **Definition** (`Models/WorkflowDefinition.cs:3-17`): `Name`, `TriggerContentType`, `TriggerEvent`, `Conditions: Dictionary<string,string>` (AND-only equality), `Actions: List<WorkflowAction>` where each action is `Type` + `Parameters: Dictionary<string,string>`.
- **Actions** (`Features/Workflows/Actions/`, DI-registered `ServiceCollectionExtensions.cs:282-287`): `Email`, `SMS`, `Webhook` (with a solid SSRF guard), `CreateTask`, `UpdateField`, `Conditional`. Contract: `IWorkflowAction` = `Type` + `ExecuteAsync(params, content, ct) → void` (`IWorkflowAction.cs:7-22`).
- **Execution** (`WorkflowEngine.cs:23-56, 81-112`): sequential `foreach`; per-workflow and per-action try/catch that **swallows and logs** failures. Template vars via `TemplateVariableExtractor` (`{{data.Field}}`, `{{status}}`, …).
- **Extensibility:** custom **actions** yes (implement `IWorkflowAction`, register in DI; optional `[WorkflowActionMetadata]`). Custom **triggers** no (hardcoded in the projection).
- **Multi-tenancy:** conjoined; `WorkflowDefinition`/`WorkflowExecutionLog` are tenant-scoped (correct).

## 2. Gaps & risks

**Trust (highest priority):**
1. **No idempotency / replay safety.** Async projection reprocessing (rebuild/replay) re-fires all side-effecting actions, so duplicate emails/webhooks/content. No dedupe key. (`WorkflowEngine.cs`)
2. **Blind in production.** `WorkflowExecutionLog` + `IWorkflowDebugger` exist but are wired **only to the dry-run endpoint** (`DryRunWorkflow/Endpoint.cs:65,95`). Live runs never record anything; `GET /api/workflows/{id}/debug` shows only dry-runs. Failures are swallowed (`WorkflowEngine.cs:105-110`).
3. **No retries / dead-letter** at the engine level (only HTTP-layer resilience on the webhook client).
4. **Trigger loops.** `UpdateField`/`CreateTask` emit new content events that can re-trigger workflows with **no depth/recursion guard** (`UpdateFieldAction.cs:84-85`).

**Consistency:**
5. **Two incompatible condition mechanisms.** Top-level `Conditions` = AND-only `ToString()` equality, and any missing key fails the whole workflow (`WorkflowEngine.cs:58-79`). `ConditionalAction` has a separate hand-rolled `==`/`!=` parser (`ConditionalAction.cs:96-140`). No `> < contains`, no OR, no numeric/date typing, no shared evaluator.
6. **Phantom `Deleted` trigger**, declared valid (`WorkflowEvents.cs:21`) but no `ContentDeleted` event is ever emitted.
7. **Status triggers only fire on `Published`**, no generic status-changed or per-status triggers.
8. **Doc/code drift**. Docs show `"SendEmail"` + `"config"` while code requires `"Email"` + `"Parameters"`; those examples fail validation.
9. **No update/delete/get-by-id endpoints**, definitions are write-once via API.

**Expressiveness:**
10. **String-only params/data** (`Dictionary<string,string>`), no typing, arrays, or structured payloads; `ConditionalAction` smuggles JSON inside a string.
11. **No cross-action data passing**, actions can't read prior action output.
12. **No branching beyond nested `Conditional`**, no parallelism.
13. **No scheduling/delays/waits**, flat and immediate action list only.
14. **Concurrency:** `UpdateFieldAction` writes without optimistic guards (can clobber concurrent edits).

## 3. The one architectural move that unlocks most

Change the action contract from `ExecuteAsync(Dictionary<string,string>, Content, ct) → void` to an **execution context + result**:

```
ExecuteAsync(WorkflowContext ctx, CancellationToken ct) → ActionResult

WorkflowContext { Content, TriggerEvent, TenantId, Variables (typed bag), Outputs (per prior action) }
ActionResult   { Status: Success|Failed, Outputs, Control: Continue|Halt|Skip, Error? }
```

This single change enables **cross-action data passing**, **halt-on-error**, and a natural hook to **record every run**. Everything below hangs off it.

## 4. Tiered plan

**Tier 1, trust (do first):**
- Idempotency guard: a `WorkflowRun` keyed by `(eventId, workflowId)`; skip if already ran.
- Wire the existing `WorkflowDebugger` into **live** execution → persist `WorkflowRun` (trigger, per-action status, error, timing). Makes the debug UI real (mostly connecting dead code).
- Retries + dead-letter per action (transient vs permanent) instead of swallow-and-log.
- Recursion/loop guard: cap the cause-chain depth of workflow-emitted content events.

**Tier 2, consistency and power:**
- One shared expression engine (`== != > < contains`, `AND/OR`, numeric/date typing) used by both top-level conditions and `Conditional`.
- Structured parameters (typed/JSON) + the variables bag; retire `Dictionary<string,string>`.
- Fix trigger gaps: emit a real `ContentDeleted` event; add generic/per-status triggers.
- Complete CRUD (update/delete/get-by-id) and fix doc/code drift (align action names/params or add aliases).

**Tier 3, reach (bigger, optional):**
- `ITrigger` interface so triggers are pluggable like actions → scheduled/cron + external webhook-in triggers.
- Durable waits/delays/timers ("delay 3 days", "wait until status = X"). This is the real fork into a durable workflow engine, only if long-running processes are needed.

## 5. Recommendation

Harden and expand the **rules engine**; keep the execution-context seam clean so durable waits (Tier 3) stay possible later without a rewrite. Tier 1 + Tier 2 is the sweet spot for the actual use cases (notifications, and the config-driven **accounting posting rules** ("if this event, post this"), which becomes a natural `PostJournalEntry` action once context/data-passing exist).

## 6. Decisions to make when picked up

- Do we need durable long-running workflows (waits/timers), or is immediate event→action enough? (Determines whether Tier 3 is ever in scope.)
- Idempotency key: event id vs a content-version+workflow hash?
- Run-history retention/pruning policy (per tenant).
- Backward compatibility: migrate existing `Dictionary<string,string>` definitions vs. version the schema.
