'use client';

import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { useContents } from '@/hooks/use-contents';
import {
  REQUEST_METHODS,
  SUCCESS_RULES,
  checkDraft,
  describeEntry,
  formatHeaderTemplates,
  parseHeaderTemplates,
  prettyBody,
  templateVariables,
  toDraft,
  unresolvableVariables,
  useConnectorOptions,
  useDeleteRequest,
  useDryRunRequest,
  useRequests,
  useSaveRequest,
  type DryRunResult,
  type RequestDefinition,
  type RequestDraft,
} from '@/hooks/use-requests';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import {
  IconEye,
  IconPen,
  IconPlay,
  IconPlus,
  IconTrash,
  IconWarning,
  IconWebhook,
} from '@/components/icons';

const CARD = 'bg-card rounded-xl border p-6 shadow-[var(--shadow-card)]';
const FIELD = 'mt-1 w-full rounded-md border px-3 py-2 text-[13px]';

const BLANK: RequestDraft = {
  name: '',
  slug: '',
  connectorSlug: '',
  method: 'POST',
  pathTemplate: '',
  headerTemplates: {},
  bodyTemplate: '',
  bodyContentType: 'application/json',
  querySlug: '',
  success: 'TwoHundredRange',
  successJsonPath: '',
};

/**
 * What a dry run answered, and nothing about a call having happened.
 *
 * Every line here is in the conditional. Nothing on this screen sends a request, so there is no
 * state in which this panel could be describing a delivery, and it says so twice: once in the
 * header, which is the part that stays on screen, and once beside the verdict.
 */
function DryRunResultPanel({
  result,
  contentType,
  requestName,
  entryId,
}: {
  result: DryRunResult;
  contentType: string;
  requestName: string;
  entryId: string;
}) {
  const body = prettyBody(result.body, contentType);
  const headers = Object.entries(result.headers);

  return (
    <div className="mt-5 overflow-hidden rounded-lg border">
      <div className="bg-muted/60 flex flex-wrap items-center gap-x-3 gap-y-2 border-b px-4 py-3">
        <IconEye aria-hidden className="size-4 shrink-0" />
        <span className="text-[13px] font-bold">Dry run. Nothing was sent.</span>
        <Badge variant={result.wouldSend ? 'default' : 'destructive'}>
          {result.wouldSend ? 'Would be sent' : 'Would be refused'}
        </Badge>
      </div>

      <div className="p-4">
        <p className="text-muted-foreground text-[12.5px]">
          Composed from <span className="font-bold">{requestName}</span> against entry{' '}
          <span className="font-mono">{entryId}</span>. No button on this screen sends a request; a
          workflow action is what does that.
        </p>

        {result.wouldSend ? (
          <>
            <p className="mt-4 text-[13px] font-bold">This is what would go out</p>

            <div className="mt-2 overflow-x-auto">
              <p className="font-mono text-[12.5px] whitespace-nowrap">
                <span className="font-bold">{result.method}</span> {result.url}
              </p>
            </div>

            <p className="mt-5 text-[13px] font-bold">Headers</p>
            {headers.length === 0 ? (
              <p className="text-muted-foreground mt-1 text-[13px]">
                None, beyond whatever the sender adds.
              </p>
            ) : (
              <dl className="mt-2 grid gap-1.5 text-[12.5px]">
                {headers.map(([name, value]) => (
                  <div key={name} className="flex flex-wrap gap-x-2">
                    <dt className="font-mono font-bold">{name}:</dt>
                    <dd className="font-mono break-all">{value}</dd>
                  </div>
                ))}
              </dl>
            )}
            <p className="text-muted-foreground mt-2 text-[12.5px]">
              The connector&apos;s credentials are attached after this, so none appears here even
              when the connector holds one. There is nothing in a dry run to redact.
            </p>

            <p className="mt-5 text-[13px] font-bold">Body</p>
            {body.length === 0 ? (
              <p className="text-muted-foreground mt-1 text-[13px]">No body.</p>
            ) : (
              <pre className="bg-muted/50 mt-2 max-h-80 overflow-auto rounded-md p-3 font-mono text-[12.5px]">
                {body}
              </pre>
            )}
          </>
        ) : (
          <>
            <p className="mt-4 flex items-start gap-2 text-[13px] font-bold">
              <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
              <span>Why it would not be sent</span>
            </p>
            <p className="mt-2 text-[13px]">
              {result.refusal ?? 'The server refused it and gave no reason.'}
            </p>
            <p className="text-muted-foreground mt-3 text-[12.5px]">
              A refusal is the whole point of composing first. Fix the template and run it again.
            </p>
          </>
        )}
      </div>
    </div>
  );
}

export default function RequestsPage() {
  const requests = useRequests();
  const connectors = useConnectorOptions();
  const entries = useContents({ pageSize: 50 });
  const save = useSaveRequest();
  const remove = useDeleteRequest();
  const dryRun = useDryRunRequest();

  const [draft, setDraft] = useState<RequestDraft | null>(null);
  const [isNew, setIsNew] = useState(true);
  const [headerText, setHeaderText] = useState('');

  const [dryRunSlug, setDryRunSlug] = useState('');
  const [entryId, setEntryId] = useState('');
  const [result, setResult] = useState<DryRunResult | null>(null);
  const [ranAgainst, setRanAgainst] = useState<{ request: RequestDefinition; entryId: string } | null>(
    null,
  );

  const items = requests.data?.items ?? [];
  const entryItems = entries.data?.items ?? [];
  const chosen = items.find((r) => r.slug === dryRunSlug);

  const headers = useMemo(() => parseHeaderTemplates(headerText), [headerText]);

  // Read off the draft the operator is looking at, headers included, so the list changes as they
  // type rather than after a save.
  const variables = useMemo(
    () => (draft ? templateVariables({ ...draft, headerTemplates: headers.headers }) : []),
    [draft, headers.headers],
  );
  const unresolvable = unresolvableVariables(variables);

  const problem = draft ? (headers.problem ?? checkDraft({ ...draft, headerTemplates: headers.headers })) : null;

  function openNew() {
    setDraft(BLANK);
    setIsNew(true);
    setHeaderText('');
  }

  function openExisting(request: RequestDefinition) {
    setDraft(toDraft(request));
    setIsNew(false);
    setHeaderText(formatHeaderTemplates(request.headerTemplates));
  }

  function closeEditor() {
    setDraft(null);
    setHeaderText('');
  }

  function armDryRun(request: RequestDefinition) {
    setDryRunSlug(request.slug);
    // The old result described a different request. Leaving it up beside a new selection is how
    // someone reads a verdict about the wrong thing.
    setResult(null);
    setRanAgainst(null);
  }

  async function onSave() {
    if (!draft || problem) return;

    try {
      const saved = await save.mutateAsync({ ...draft, headerTemplates: headers.headers });
      toast.success(isNew ? `Created "${saved.name}".` : `Saved "${saved.name}".`);
      closeEditor();
    } catch (error) {
      toast.error(apiErrorMessage(error, 'Could not save the request.'));
    }
  }

  async function onDelete(request: RequestDefinition) {
    if (!window.confirm(`Delete "${request.name}"? A workflow action naming it will stop working.`)) {
      return;
    }

    try {
      await remove.mutateAsync(request.slug);
      if (dryRunSlug === request.slug) {
        setDryRunSlug('');
        setResult(null);
        setRanAgainst(null);
      }
      toast.success(`Deleted "${request.name}".`);
    } catch (error) {
      toast.error(apiErrorMessage(error, 'Could not delete the request.'));
    }
  }

  async function onDryRun() {
    if (!chosen || !entryId) return;

    try {
      const answer = await dryRun.mutateAsync({ slug: chosen.slug, contentId: entryId });
      setResult(answer);
      setRanAgainst({ request: chosen, entryId });
    } catch (error) {
      setResult(null);
      setRanAgainst(null);
      toast.error(apiErrorMessage(error, 'The dry run did not complete.'));
    }
  }

  const newButton = (
    <Button size="sm" onClick={openNew}>
      <IconPlus />
      New request
    </Button>
  );

  return (
    <div className="space-y-6">
      <PageHeader
        title="Outbound requests"
        description="A request definition is an outbound call held as configuration: a connector, a method, a path and a template. Nothing on this screen sends one."
        actions={newButton}
      />

      <section className={CARD} aria-labelledby="dry-run-heading">
        <h2 id="dry-run-heading" className="flex items-center gap-2 text-[15px] font-bold">
          <IconEye aria-hidden className="size-4" />
          Dry run
        </h2>
        <p className="text-muted-foreground mt-1.5 text-[13px]">
          Composes a request against a real entry and shows you the finished call: the URL, the
          headers and the body a provider would receive. It stops there. This is how you find out
          what a template produces while you can still change it.
        </p>

        <div className="mt-4 grid gap-4 md:grid-cols-2">
          <div>
            <Label htmlFor="dry-run-request">Request</Label>
            <select
              id="dry-run-request"
              className={FIELD}
              value={dryRunSlug}
              onChange={(e) => {
                setDryRunSlug(e.target.value);
                setResult(null);
                setRanAgainst(null);
              }}
            >
              <option value="">Choose a request</option>
              {items.map((request) => (
                <option key={request.slug} value={request.slug}>
                  {request.name} ({request.method} {request.pathTemplate})
                </option>
              ))}
            </select>
          </div>

          <div>
            <Label htmlFor="dry-run-entry">Entry to compose against</Label>
            <select
              id="dry-run-entry"
              className={FIELD}
              value={entryId}
              onChange={(e) => {
                setEntryId(e.target.value);
                setResult(null);
                setRanAgainst(null);
              }}
            >
              <option value="">Choose an entry</option>
              {entryItems.map((entry) => (
                <option key={entry.id} value={entry.id}>
                  {describeEntry(entry)}
                </option>
              ))}
            </select>
            {entryId && <p className="text-muted-foreground mt-1 font-mono text-[12px]">{entryId}</p>}
          </div>
        </div>

        {!entries.isLoading && entryItems.length === 0 && (
          <p className="mt-3 flex items-start gap-2 text-[13px]">
            <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
            <span>
              A dry run composes against a real entry, and there are none to compose against yet.
            </span>
          </p>
        )}

        <Button className="mt-4" disabled={!chosen || !entryId || dryRun.isPending} onClick={() => void onDryRun()}>
          <IconPlay />
          {dryRun.isPending ? 'Composing...' : 'Compose it, do not send it'}
        </Button>

        {result && ranAgainst && (
          <DryRunResultPanel
            result={result}
            contentType={ranAgainst.request.bodyContentType}
            requestName={ranAgainst.request.name}
            entryId={ranAgainst.entryId}
          />
        )}
      </section>

      {draft && (
        <section className={CARD} aria-labelledby="editor-heading">
          <h2 id="editor-heading" className="text-[15px] font-bold">
            {isNew ? 'New request' : `Edit ${draft.name || draft.slug}`}
          </h2>
          <p className="text-muted-foreground mt-1.5 text-[13px]">
            Saving stores configuration. It does not call anything.
          </p>

          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label htmlFor="request-name">Name</Label>
              <Input
                id="request-name"
                value={draft.name}
                placeholder="Post to the company status page"
                onChange={(e) => setDraft({ ...draft, name: e.target.value })}
              />
            </div>

            <div>
              <Label htmlFor="request-slug">Slug</Label>
              <Input
                id="request-slug"
                value={draft.slug}
                readOnly={!isNew}
                placeholder="status-page-incident"
                onChange={(e) => setDraft({ ...draft, slug: e.target.value })}
              />
              <p className="text-muted-foreground mt-1 text-[12px]">
                {isNew
                  ? 'What a workflow action will name. Lowercase letters, digits and hyphens.'
                  : 'Fixed once saved: a workflow action names it, and changing it here would save a second request instead.'}
              </p>
            </div>

            <div>
              <Label htmlFor="request-connector">Connector</Label>
              <select
                id="request-connector"
                className={FIELD}
                value={draft.connectorSlug}
                onChange={(e) => setDraft({ ...draft, connectorSlug: e.target.value })}
              >
                <option value="">Choose a connector</option>
                {connectors.data?.map((connector) => (
                  <option key={connector.slug} value={connector.slug}>
                    {connector.name} ({connector.baseUrl})
                  </option>
                ))}
              </select>
              {connectors.data?.some((c) => c.slug === draft.connectorSlug && !c.enabled) && (
                <p className="mt-1 flex items-start gap-2 text-[12.5px]">
                  <IconWarning aria-hidden className="mt-0.5 size-3.5 shrink-0" />
                  <span>That connector is disabled, so a workflow using this will not send.</span>
                </p>
              )}
            </div>

            <div>
              <Label htmlFor="request-method">Method</Label>
              <select
                id="request-method"
                className={FIELD}
                value={draft.method}
                onChange={(e) => setDraft({ ...draft, method: e.target.value })}
              >
                {REQUEST_METHODS.map((method) => (
                  <option key={method} value={method}>
                    {method}
                  </option>
                ))}
              </select>
            </div>
          </div>

          <div className="mt-4">
            <Label htmlFor="request-path">Path</Label>
            <Input
              id="request-path"
              value={draft.pathTemplate}
              placeholder="/v1/incidents/{{Slug}}"
              onChange={(e) => setDraft({ ...draft, pathTemplate: e.target.value })}
            />
            <p className="text-muted-foreground mt-1 text-[12px]">
              Joined to the connector&apos;s base URL. A value substituted here is escaped for a URL.
            </p>
          </div>

          <div className="mt-4">
            <Label htmlFor="request-headers">Headers</Label>
            <Textarea
              id="request-headers"
              className="mt-1 font-mono text-[12.5px]"
              rows={4}
              value={headerText}
              placeholder={'Accept: application/json\nX-Source: barakoCMS'}
              onChange={(e) => setHeaderText(e.target.value)}
            />
            <p className="text-muted-foreground mt-1 text-[12px]">
              One per line, as <span className="font-mono">Name: value</span>.
            </p>
            {headers.problem && (
              <p className="mt-1 flex items-start gap-2 text-[12.5px]">
                <IconWarning aria-hidden className="mt-0.5 size-3.5 shrink-0" />
                <span>{headers.problem}</span>
              </p>
            )}
          </div>

          <div className="mt-4 grid gap-4 md:grid-cols-2">
            <div>
              <Label htmlFor="request-content-type">Body content type</Label>
              <Input
                id="request-content-type"
                value={draft.bodyContentType}
                onChange={(e) => setDraft({ ...draft, bodyContentType: e.target.value })}
              />
              <p className="text-muted-foreground mt-1 text-[12px]">
                A JSON type makes each substituted value escape as JSON, and the composed body is
                parsed before anything is sent.
              </p>
            </div>

            <div>
              <Label htmlFor="request-success">Success is</Label>
              <select
                id="request-success"
                className={FIELD}
                value={draft.success}
                onChange={(e) => setDraft({ ...draft, success: e.target.value })}
              >
                {SUCCESS_RULES.map((rule) => (
                  <option key={rule.value} value={rule.value}>
                    {rule.label}
                  </option>
                ))}
              </select>
              <p className="text-muted-foreground mt-1 text-[12px]">
                {SUCCESS_RULES.find((rule) => rule.value === draft.success)?.description}
              </p>
            </div>
          </div>

          {draft.querySlug.length > 0 && (
            <div className="mt-4">
              <Label htmlFor="request-query">Named query</Label>
              <Input id="request-query" value={draft.querySlug} readOnly />
              <p className="text-muted-foreground mt-1 text-[12px]">
                Set through the API. This screen shows it and saves it back unchanged; a template
                still cannot read a query yet (#328).
              </p>
            </div>
          )}

          {draft.success === 'TwoHundredAndJsonPathAbsent' && (
            <div className="mt-4">
              <Label htmlFor="request-json-path">Path that must be absent</Label>
              <Input
                id="request-json-path"
                value={draft.successJsonPath}
                placeholder="error.code"
                onChange={(e) => setDraft({ ...draft, successJsonPath: e.target.value })}
              />
            </div>
          )}

          <div className="mt-4">
            <Label htmlFor="request-body">Body</Label>
            <Textarea
              id="request-body"
              className="mt-1 font-mono text-[12.5px]"
              rows={8}
              value={draft.bodyTemplate}
              placeholder={'{\n  "title": "{{Title}}",\n  "url": "{{publicurl}}"\n}'}
              onChange={(e) => setDraft({ ...draft, bodyTemplate: e.target.value })}
            />
          </div>

          <div className="mt-4 rounded-lg border p-4">
            <p className="text-[13px] font-bold">Variables in this request</p>
            {variables.length === 0 ? (
              <p className="text-muted-foreground mt-1 text-[13px]">
                None. Every call this makes would be identical whatever entry it runs against.
              </p>
            ) : (
              <div className="mt-2 flex flex-wrap gap-1.5">
                {variables.map((name) => (
                  <Badge
                    key={name}
                    variant={unresolvable.includes(name) ? 'destructive' : 'secondary'}
                    className="font-mono"
                  >
                    {name}
                  </Badge>
                ))}
              </div>
            )}

            {unresolvable.length > 0 && (
              <p className="mt-3 flex items-start gap-2 text-[12.5px]">
                <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
                <span>
                  A request template cannot read a named query yet (#328), so the server refuses one
                  rather than posting the literal text to a provider. Remove it.
                </span>
              </p>
            )}

            <p className="text-muted-foreground mt-3 text-[12.5px]">
              A field that is not Public is refused too, at compose time, rather than sent redacted.
              A dry run is where you see which one.
            </p>
          </div>

          {problem && (
            <p className="mt-4 flex items-start gap-2 text-[13px]">
              <IconWarning aria-hidden className="mt-0.5 size-4 shrink-0" />
              <span>{problem}</span>
            </p>
          )}

          <div className="mt-4 flex flex-wrap gap-2">
            <Button disabled={problem !== null || save.isPending} onClick={() => void onSave()}>
              {save.isPending ? 'Saving...' : isNew ? 'Create request' : 'Save changes'}
            </Button>
            <Button variant="ghost" onClick={closeEditor}>
              Cancel
            </Button>
          </div>
        </section>
      )}

      {requests.isLoading ? (
        <TableSkeleton />
      ) : requests.isError ? (
        <ErrorState entity="requests" onRetry={() => void requests.refetch()} />
      ) : items.length === 0 ? (
        <EmptyState
          icon={IconWebhook}
          title="No requests yet"
          description="A request definition is what a workflow action sends through a connector. Add one, then dry run it before it ever goes out."
          action={newButton}
        />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Call</TableHead>
                <TableHead>Connector</TableHead>
                <TableHead>Success</TableHead>
                <TableHead className="w-32" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map((request) => (
                <TableRow key={request.slug}>
                  <TableCell>
                    <span className="font-medium">{request.name}</span>
                    <span className="text-muted-foreground block font-mono text-xs">
                      {request.slug}
                    </span>
                  </TableCell>
                  <TableCell className="font-mono text-xs">
                    <span className="font-bold">{request.method}</span> {request.pathTemplate}
                  </TableCell>
                  <TableCell className="font-mono text-xs">{request.connectorSlug}</TableCell>
                  <TableCell className="text-xs">
                    {SUCCESS_RULES.find((rule) => rule.value === request.success)?.label ??
                      request.success}
                  </TableCell>
                  <TableCell>
                    <div className="flex justify-end gap-1">
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label={`Dry run ${request.name}`}
                        onClick={() => armDryRun(request)}
                      >
                        <IconPlay className="size-3.5" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label={`Edit ${request.name}`}
                        onClick={() => openExisting(request)}
                      >
                        <IconPen className="size-3.5" />
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        aria-label={`Delete ${request.name}`}
                        className="text-destructive hover:text-destructive"
                        onClick={() => void onDelete(request)}
                      >
                        <IconTrash className="size-3.5" />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}
