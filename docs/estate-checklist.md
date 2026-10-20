# Can this API sit inside your estate

A checklist for an architect. barakoCMS is a modular monolith on one PostgreSQL database, on purpose.
It is not a microservice. What it offers an estate is the behaviour of a well run participant: it
publishes changes, it appears in your traces, a credential can be scoped to one job, one consumer
cannot starve the others, and the contract is versioned.

Each answer names the test or code that backs it. A "no" or "partly" says what is missing, so the
list does not read better than the product is.

## 1. Does it publish changes I can subscribe to

**Yes, from a single instance.**

| What | Where | Backed by |
|---|---|---|
| Public server-sent event stream of published content | `GET /api/public/events`, [delivery-api.md](delivery-api.md) | `ContentEventStreamTests` |
| Authenticated change feed you page by sequence | `GET /api/changes`, [realtime.md](realtime.md) | `ChangeFeedTests` (paging resumes without loss or repeat, a late commit is not skipped, other tenants are not returned) |
| Notices over SignalR | `/hubs/realtime`, [realtime.md](realtime.md) | `RealtimeHubTests`, `RealtimeNoticeTests` |
| Signed outbound webhooks | [webhooks.md](webhooks.md) | `WebhookDeliveryTests.A_signed_delivery_verifies_with_the_documented_recipe_and_not_with_another_secret` |
| A delivery log with retention | [webhooks.md](webhooks.md) | `WebhookDeliveryTests.The_sweep_removes_an_old_delivery_and_keeps_a_young_one` |

Limits you should know about:

- The SSE stream and the SignalR notices come from the instance that committed the write
  (`ContentChangeListener`, [realtime.md](realtime.md)). A subscriber on instance A does not hear a
  write made through instance B. With more than one instance, use `GET /api/changes`, which reads the
  event store and is the same on every instance, and treat the notice as a hint to read it.
- There is no broker. Whether to add one is open in #688.
- There is no inbound webhook receiver in the core. `BarakoCMS.Email.Resend` has its own endpoint
  with its own signature check. This is open in #690.

## 2. Does it appear in my traces

**Yes, when an OTLP endpoint is configured. Off by default.**

A caller's `traceparent` is honoured, the request span joins the caller's trace, and outbound calls
carry the trace on to the receiver. Details are in [tracing.md](tracing.md).

| What | Backed by |
|---|---|
| A `traceparent` is kept and the correlation id falls back to its trace id | `CorrelationIdMiddlewareTests.With_no_id_from_the_caller_the_trace_id_of_the_request_is_used`, `CorrelationTests.A_W3C_traceparent_is_kept` |
| The trace is stored on the events a request writes | `EventCorrelationTests.A_traceparent_sent_with_a_request_is_on_the_event_the_request_stores` |
| A workflow action span sits under the request that caused it | `WorkflowActionSpanTests.A_workflow_action_span_sits_under_the_request_that_caused_its_run` |
| An outbound call carries the trace to the receiver | `WorkflowActionSpanTests.An_outbound_call_made_while_the_action_runs_carries_the_trace_to_the_receiver` |
| Each retry of an outbound call is visible | `OutboundRetrySpanTests.A_retried_call_shows_each_try_and_each_retry` |
| A job run has a span | `JobRunSpanTests` |
| Spans carry ids and names, not values | `SpanScrubberTests`, `WorkflowActionSpanTests.A_workflow_action_span_carries_ids_and_names_and_nothing_from_the_action` |
| No endpoint means no tracer and no connection | `TracingSetupTests.With_no_endpoint_AddBarakoCMS_registers_no_tracer` |
| A collector that never answers does not hold a request | `TracingExportTests.Ending_a_span_does_not_wait_for_a_collector_that_never_answers` |

If the collector is down, spans are dropped, not queued on disk ([tracing.md](tracing.md)).

## 3. Can I scope a credential to one service

**Yes, to a job on the content and files surface. Not yet to everything.**

An API key acts as its owner and cannot exceed the owner's roles. Its scopes narrow it further.
[access-control.md](access-control.md) lists them.

| What | Backed by |
|---|---|
| A write key cannot erase or roll back content | `ApiKeyIntegrationTests.WriteKey_CannotEraseContent`, `WriteKey_CannotRollBackContent` |
| Erase and rollback need `content:destructive` (or `*`) | `ApiKeyIntegrationTests.DestructiveOrWildcardKey_CanEraseContent` |
| A read of a content type named `erase` or `rollback` is not mistaken for a destructive call | `ApiKeyIntegrationTests.ReadWriteKey_ReadingBySlugFromATypeNamedEraseOrRollback_IsNotTreatedAsDestructive` |
| An unknown scope is refused at creation | `ApiKeyIntegrationTests.Create_RejectsUnknownScope` |
| A key can be limited to named content types (push keys) | [collection-push.md](collection-push.md) |
| A key rotates with an overlap window | `POST /api/api-keys/{id}/rotate`, [api-key-service-principals.md](api-key-service-principals.md) |

Not there yet, from [api-key-service-principals.md](api-key-service-principals.md): scopes for roles,
workflows, connectors, module endpoints, the pages tree and export, and a service principal that
owns keys and does not depend on the admin who made them.

## 4. Can I stop one consumer starving the others

**Yes, if you set the limits. They are off by default so existing deployments do not change.**

| What | Setting | Backed by |
|---|---|---|
| Delivery reads limited per client address | `RateLimiting:Delivery` | `NamedRateLimitPolicyTests.A_configured_delivery_limit_refuses_the_third_read_from_one_address` |
| A quota per API key, whatever address it comes from | `RateLimiting:ApiKey` | `NamedRateLimitPolicyTests.A_configured_quota_refuses_the_third_request_of_one_key_and_no_other_caller`, `The_api_key_quota_follows_the_key_whatever_address_it_comes_from` |
| Your own policies, per address, user or key | `RateLimiting:Policies:{name}` | `NamedRateLimitPolicyTests.A_policy_partitioned_by_api_key_counts_each_key_and_puts_everyone_else_in_one_bucket` |
| Your renderer in its own bucket | `RateLimiting:Renderer` | `RateLimitSetupTests` |
| Nothing configured changes nothing | | `NamedRateLimitPolicyTests.With_nothing_configured_every_limit_is_the_number_it_was_and_nothing_new_is_on` |
| A retry does not repeat a write | `Idempotency-Key` on `POST`, `PUT`, `PATCH`, [idempotency.md](idempotency.md) | `IdempotencyTests.a_content_post_replayed_with_the_same_key_returns_the_first_response_and_creates_one_entry` |
| A consumer can poll cheaply | weak `ETag`, `If-None-Match`, and `changedSince` on lists, [delivery-api.md](delivery-api.md) | `DeliveryCacheClassTests.Every_delivery_route_answers_its_class_a_weak_etag_and_tags_under_its_tenant_and_304_on_a_match`, `DeliveryCacheTests.If_none_match_uses_weak_comparison` |

Limits you should know about:

- Every limit is counted in the memory of one process ([deploy-in-production.md](deploy-in-production.md)).
  Two instances behind a load balancer let a client spend each limit twice.
- One quota number applies to every key. A key cannot be given its own.
- Reads scale by growing the primary. There is no read replica support (#564).

## 5. Is the contract versioned

**Yes, on every response, and diffed in CI.**

| What | Backed by |
|---|---|
| `X-Api-Contract-Version` (admin) and `X-Delivery-Contract-Version` (delivery) on every response | `ContractSurfaceVersionTests.Every_response_carries_both_versions_each_on_its_own_header` |
| The header is present on a 401 | `ApiContractVersionHeaderTests.The_header_is_present_even_on_the_meta_endpoints_own_401` |
| The two numbers are pinned | `ContractSurfaceVersionTests.The_two_core_surfaces_are_pinned_to_their_numbers` |
| A module has its own number, listed by `/api/meta/describe` | `ModuleHttpContractVersionTests` |
| A published OpenAPI document, including delivery routes | `DeliveryOpenApiTests` |
| A breaking change to the document fails CI | `openapi/baseline.json`, `scripts/openapi-contract-check.sh`, `scripts/test-openapi-gate.sh` |

What counts as breaking, and which number moves, is in section 6 of `CLAUDE.md`.

## Also worth checking

| Question | Answer | Backed by |
|---|---|---|
| Does the orchestrator get a liveness and a readiness probe | Yes | `HealthProbeTests.A_failing_database_check_fails_readiness_and_leaves_liveness_healthy` |
| Does readiness stay closed until the data seed finishes | Yes | `HealthProbeTests.The_readiness_probe_is_closed_while_the_startup_seed_is_pending` |
| Can a request be followed through the logs without tracing | Yes, `X-Correlation-ID` | `CorrelationIdMiddlewareTests` |

## Summary

| Question | Answer |
|---|---|
| Publishes changes I can subscribe to | Yes, per instance. The change feed works across instances. |
| Appears in my traces | Yes, with an OTLP endpoint set |
| Credential scoped to one service | Yes for content and files. Other surfaces are open. |
| Stop one consumer starving the others | Yes when configured, per process |
| Versioned contract | Yes |
