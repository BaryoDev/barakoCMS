`GET /api/public/events` streams `content.published`, `content.updated` and `content.unpublished` for the tenant as server-sent events, filterable with a repeatable `?type=`. Anonymous like the rest of public delivery. Every payload is produced by `PublicDelivery.ToPublic`, the projection the REST reads use, so there is no second copy of the masking rules: only publicly deliverable types, only while Published, only Public fields. An unpublish carries id, type and slug and nothing else.

Off by default. `Delivery:Events:Enabled` turns it on and the route is 404 while it is off (the same answer the OAuth start endpoint gives an unconfigured provider). `Delivery:Events:MaxConnections` (100) caps open streams per instance with a 503 and `Retry-After` beyond it. A `: keepalive` comment line goes out after `Delivery:Events:KeepAliveSeconds` (15) of silence.

Transport is an in-process broadcaster over `System.Threading.Channels`, keyed by tenant slug, one bounded channel of 64 per connection, drop-oldest with one warning per connection. It lives in core because it needs the projection. It hooks in as a Marten session listener on `AfterCommitAsync`, not in `WorkflowProjection`: the async daemon runs on one instance, so a stream fed from there would reach only that instance's subscribers. `docs/delivery-api.md` states the caps and that with several API instances each streams only the writes it handled.

Proof: `ContentEventStreamTests`, 10 tests, all green (`dotnet run --project BarakoCMS.Tests/BarakoCMS.Tests.csproj --no-build -- -class BarakoCMS.Tests.ContentEventStreamTests`). Fail-first, each mutation applied, run, reverted:

- tenant: broadcaster made to ignore the tenant key (`HasSubscribers` answers on any connection, `Publish` fans out to every tenant). `A_change_on_another_tenant_never_reaches_a_subscriber` failed: the first frame carried tenant B's slug.
- sensitivity: listener made to publish the raw document instead of `ToPublic`. `A_publish_through_the_api_streams_the_public_fields_and_not_a_sensitive_one` failed on `"Secret":"topsecret"` in the frame.
- Published gate: listener made to project with `allowUnpublished: true`. `A_draft_save_streams_nothing` failed: the first frame was the draft.
- config gate: `Enabled` check removed. `The_stream_is_404_until_enabled` failed.

Decisions

- The keepalive is an SSE comment written straight to the response between items. `Send.EventStreamAsync` has no comment frame, and the writer flushes before it waits on the enumerator, so nothing of its own is pending when the comment goes out.
- A draft moved to Archived emits nothing: it was never public, and an unpublish for it would hand out a slug the REST read answers 404 for. A published entry moved to Draft or Archived emits `content.unpublished`.
- At most 20 `type` values per connection, 400 beyond that, so a client cannot make the server hold a large set per connection.
- Erasing an entry emits nothing yet. It is not a content event the listener sees, and the docs say so.
- Content types running with `EventSourcing:DocumentTypesAppend` off write no events, so nothing about them is streamed, the same way nothing about them fires a workflow.

Fixes #105

Review additions

- Raising a field from Public to Sensitive streamed the field one more time. `SetFieldSensitivity` appends `ContentFieldSensitivityChanged` to each entry and commits those batches before it stores the type (so a failure part way leaves the field readable rather than in anonymous search), and the listener read the definition as it stood, still Public. The listener now projects with the sensitivity the commit's own event declares, on a copy of the definition, so `ToPublic` stays the only masking rule. `Raising_a_field_to_sensitive_streams_the_entry_without_that_field` failed on the old listener with `"Title":"Control ..."` in the `content.updated` frame and passes now; the class is 11 green.
- The docs said the frames carry no `id`; the FastEndpoints writer emits an empty `id:` line, and the docs now say that.


Follow-up filed from review: #520 (a per-client connection cap under the instance cap).
