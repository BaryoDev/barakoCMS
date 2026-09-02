'use client';

import { use, useState } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useAuth } from '@/hooks/use-auth';
import { useSchemas } from '@/hooks/use-schemas';
import {
  useContent,
  useContentHistory,
  useRollbackContent,
  useUpdateContent,
  useScheduleContent,
  useUpdateContentStatus,
} from '@/hooks/use-contents';
import { apiErrorMessage } from '@/lib/api';
import { ContentStatus, SENSITIVITY_META, statusMeta } from '@/types/content';
import type { ContentDetail } from '@/types/content';
import { PageHeader } from '@/components/patterns/page-header';
import { StatusBadge } from '@/components/patterns/status-badge';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
import { DynamicForm } from '@/components/content/dynamic-form';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Separator } from '@/components/ui/separator';
import { IconArchive, IconHistory, IconRollback } from '@/components/icons';
import { format } from 'date-fns';
import { contentTitle } from '@/lib/content-title';

export default function ContentDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const router = useRouter();
  const { user } = useAuth();
  const { data: schemas } = useSchemas();
  const { data: content, isLoading } = useContent(id);
  const updateContent = useUpdateContent();
  const updateStatus = useUpdateContentStatus();

  const [values, setValues] = useState<Record<string, unknown>>({});
  const [tab, setTab] = useState('edit');

  // Re-seed the form whenever a different server version arrives (initial load,
  // rollback, concurrent edit) — render-time state adjustment, not an effect.
  const [seededVersion, setSeededVersion] = useState<number | null>(null);
  if (content && seededVersion !== content.version) {
    setSeededVersion(content.version);
    setValues(content.data);
  }

  const schema = schemas?.find((s) => s.name === content?.contentType);
  const canRollback = user?.roles.some((r) => r === 'SuperAdmin' || r === 'Admin') ?? false;

  if (isLoading || !content) return <TableSkeleton />;

  const meta = statusMeta(content.status);
  const sensitivityMeta = SENSITIVITY_META[content.sensitivity];

  const save = (status?: ContentStatus) => {
    updateContent.mutate(
      {
        id,
        data: {
          data: values,
          status: status ?? content.status,
          version: content.version,
        },
      },
      {
        onSuccess: () => toast.success(status === ContentStatus.Published ? 'Published' : 'Changes saved'),
        onError: (error) => toast.error(apiErrorMessage(error, 'The entry could not be saved.')),
      }
    );
  };

  const setStatus = (status: ContentStatus, label: string) => {
    updateStatus.mutate(
      { id, status },
      {
        onSuccess: () => toast.success(label),
        onError: (error) => toast.error(apiErrorMessage(error, 'The status could not be changed.')),
      }
    );
  };

  const title = contentTitle(content.data, id);

  return (
    <>
      <PageHeader
        title={title}
        description={`${schema?.displayName ?? content.contentType} · ${sensitivityMeta?.label ?? 'Public'} · version ${content.version}`}
        actions={
          <div className="flex items-center gap-2">
            <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>
            {content.status !== ContentStatus.Published && (
              <Button
                size="sm"
                onClick={() => setStatus(ContentStatus.Published, 'Published')}
                disabled={updateStatus.isPending}
              >
                Publish
              </Button>
            )}
            {content.status !== ContentStatus.Archived && (
              <ConfirmDialog
                trigger={
                  <Button variant="outline" size="sm" disabled={updateStatus.isPending}>
                    <IconArchive className="size-3.5" />
                    Archive
                  </Button>
                }
                title="Archive this entry?"
                description="Archived entries stay in the system and can be republished later. There is no delete — archiving is how entries retire."
                confirmLabel="Archive"
                onConfirm={() => setStatus(ContentStatus.Archived, 'Archived')}
              />
            )}
          </div>
        }
      />

      <Tabs value={tab} onValueChange={setTab}>
        <TabsList>
          <TabsTrigger value="edit">Edit</TabsTrigger>
          <TabsTrigger value="schedule">Schedule</TabsTrigger>
          <TabsTrigger value="history">
            <IconHistory className="size-3.5" />
            History
          </TabsTrigger>
        </TabsList>

        <TabsContent value="edit" className="mt-4 max-w-2xl">
          {schema ? (
            <>
              <DynamicForm fields={schema.fields} values={values} onChange={setValues} />
              <Separator className="my-6" />
              <div className="flex items-center gap-2">
                <Button onClick={() => save()} disabled={updateContent.isPending}>
                  {updateContent.isPending ? 'Saving…' : 'Save changes'}
                </Button>
                <Button variant="ghost" onClick={() => router.push('/content')}>
                  Back to entries
                </Button>
              </div>
            </>
          ) : (
            <p className="text-muted-foreground text-sm">
              The content type “{content.contentType}” is not readable with your role, so the fields
              cannot be edited here.
            </p>
          )}
        </TabsContent>

        <TabsContent value="schedule" className="mt-4">
          <SchedulePanel
            // Keyed by the armed times, so saving and refetching remounts the panel with the
            // server's answer. useState initialisers run once, and without this the inputs keep
            // their pre-save values while the summary above them shows the new ones. The endpoint
            // replaces both times, so editing one and saving would then send the stale other back.
            key={`${content.scheduledPublishAt ?? ''}|${content.scheduledUnpublishAt ?? ''}`}
            content={content}
          />
        </TabsContent>

        <TabsContent value="history" className="mt-4">
          <HistoryPanel id={id} active={tab === 'history'} canRollback={canRollback} />
        </TabsContent>
      </Tabs>
    </>
  );
}

/**
 * Arm or clear the times the background sweeper acts on.
 *
 * The panel shows what is armed as well as setting it. Arming a publish time and having no way to
 * read it back means the only way to know it took is to wait and see whether it happened, which is
 * why the Get endpoint returns these alongside the entry.
 *
 * Times are entered in the browser's zone and sent as UTC. `datetime-local` has no zone at all, so
 * the conversion has to be explicit: `new Date(local).toISOString()`. Treating the string as if it
 * were already UTC is the mistake this makes easy, and it is silently wrong by the reader's offset.
 */
function SchedulePanel({ content }: { content: ContentDetail }) {
  const scheduleContent = useScheduleContent();

  const [publishAt, setPublishAt] = useState(toLocalInput(content.scheduledPublishAt));
  const [unpublishAt, setUnpublishAt] = useState(toLocalInput(content.scheduledUnpublishAt));

  const armed = content.scheduledPublishAt || content.scheduledUnpublishAt;

  // The server refuses this too. Saying so here as well means the person gets told before the
  // round trip, and the server stays the one that actually enforces it.
  const inverted =
    publishAt !== '' && unpublishAt !== '' && new Date(unpublishAt) <= new Date(publishAt);

  const save = (next: { publish: string; unpublish: string }) =>
    scheduleContent.mutate(
      {
        id: content.id,
        schedule: {
          scheduledPublishAt: next.publish === '' ? null : new Date(next.publish).toISOString(),
          scheduledUnpublishAt: next.unpublish === '' ? null : new Date(next.unpublish).toISOString(),
        },
      },
      {
        onSuccess: () =>
          toast.success(
            next.publish === '' && next.unpublish === '' ? 'Schedule cleared' : 'Schedule saved',
          ),
        onError: (error: unknown) => toast.error(apiErrorMessage(error)),
      },
    );

  return (
    <div className="max-w-2xl space-y-6">
      <div>
        <h3 className="text-sm font-medium">Scheduled publishing</h3>
        <p className="text-muted-foreground mt-1 text-sm">
          A background sweep publishes a draft at its publish time and archives a published entry at
          its unpublish time. Leave a field empty to arm nothing.
        </p>
      </div>

      {armed ? (
        <p className="text-sm" data-testid="schedule-armed">
          {content.scheduledPublishAt
            ? `Publishing ${format(new Date(content.scheduledPublishAt), 'PPpp')}. `
            : ''}
          {content.scheduledUnpublishAt
            ? `Archiving ${format(new Date(content.scheduledUnpublishAt), 'PPpp')}.`
            : ''}
        </p>
      ) : (
        <p className="text-muted-foreground text-sm" data-testid="schedule-armed">
          Nothing is scheduled for this entry.
        </p>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-2">
          <Label htmlFor="publishAt">Publish at</Label>
          <Input
            id="publishAt"
            type="datetime-local"
            value={publishAt}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) => setPublishAt(e.target.value)}
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="unpublishAt">Archive at</Label>
          <Input
            id="unpublishAt"
            type="datetime-local"
            value={unpublishAt}
            onChange={(e: React.ChangeEvent<HTMLInputElement>) => setUnpublishAt(e.target.value)}
          />
        </div>
      </div>

      {inverted && (
        <p className="text-destructive text-sm" role="alert" data-testid="schedule-inverted">
          Archive time has to be after publish time, or the entry would retire before it went live.
        </p>
      )}

      <div className="flex items-center gap-2">
        <Button
          onClick={() => save({ publish: publishAt, unpublish: unpublishAt })}
          disabled={scheduleContent.isPending || inverted}
        >
          {scheduleContent.isPending ? 'Saving…' : 'Save schedule'}
        </Button>
        <Button
          variant="ghost"
          disabled={scheduleContent.isPending || (!armed && publishAt === '' && unpublishAt === '')}
          onClick={() => {
            setPublishAt('');
            setUnpublishAt('');
            save({ publish: '', unpublish: '' });
          }}
        >
          Clear schedule
        </Button>
      </div>
    </div>
  );
}

/**
 * A UTC instant as the local wall-clock string `datetime-local` expects.
 *
 * `toISOString().slice(0, 16)` is the tempting one-liner and it is wrong: it renders the UTC clock
 * into a control the browser reads as local, so the value shifts by the reader's offset every time
 * the form is opened and saved.
 */
function toLocalInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  const local = new Date(d.getTime() - d.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function HistoryPanel({
  id,
  active,
  canRollback,
}: {
  id: string;
  active: boolean;
  canRollback: boolean;
}) {
  const { data: versions, isLoading } = useContentHistory(id, active);
  const rollback = useRollbackContent();

  if (!active) return null;
  if (isLoading) return <TableSkeleton rows={3} />;
  if (!versions?.length) {
    return <p className="text-muted-foreground py-8 text-center text-sm">No earlier versions recorded.</p>;
  }

  // The API returns entries oldest-first; show newest first so the top card is the current state.
  const ordered = [...versions].reverse();

  // Not every entry is a document version any more. The endpoint used to report only creates and
  // updates and silently drop the rest, so publishing left no trace in a document's own history.
  // Now a status change, a schedule and a sensitivity change each appear, and only the two that
  // carry a document can be restored to.
  const describe = (v: (typeof ordered)[number]) => {
    switch (v.changeType) {
      case 'Created': return 'Created';
      case 'Updated': return 'Edited';
      case 'StatusChanged': return v.status ? `Status set to ${v.status}` : 'Status changed';
      case 'Scheduled': return 'Scheduling changed';
      case 'SensitivityChanged':
        return v.sensitivity ? `Sensitivity set to ${v.sensitivity}` : 'Sensitivity changed';
      default: return v.changeType;
    }
  };

  return (
    <ol className="space-y-3">
      {ordered.map((version, i) => (
        <li key={version.versionId} className="rounded-lg border p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <p className="text-sm font-medium">
                {i === 0 ? `${describe(version)} (current)` : describe(version)}
              </p>
              <p className="text-muted-foreground text-xs">
                {version.lastModifiedBy ? `By ${version.lastModifiedBy} · ` : ''}
                {format(new Date(version.timestamp), 'PPpp')}
              </p>
            </div>
            {i > 0 && canRollback && version.data && (
              <ConfirmDialog
                trigger={
                  <Button variant="outline" size="sm" disabled={rollback.isPending}>
                    <IconRollback className="size-3.5" />
                    Restore this version
                  </Button>
                }
                title="Restore this version?"
                description="The entry's fields go back to this version's values. The change is recorded as a new version, so nothing is lost."
                confirmLabel="Restore"
                onConfirm={() =>
                  rollback.mutate(
                    { id, versionId: version.versionId },
                    {
                      onSuccess: () => toast.success('Version restored'),
                      onError: (error) =>
                        toast.error(apiErrorMessage(error, 'The version could not be restored.')),
                    }
                  )
                }
              />
            )}
          </div>
          {version.data && (
            <pre className="bg-muted text-muted-foreground mt-3 max-h-48 overflow-auto rounded-md p-3 font-mono text-xs">
              {JSON.stringify(version.data, null, 2)}
            </pre>
          )}
        </li>
      ))}
    </ol>
  );
}
