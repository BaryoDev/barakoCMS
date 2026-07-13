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
  useUpdateContentStatus,
} from '@/hooks/use-contents';
import { apiErrorMessage } from '@/lib/api';
import { ContentStatus, SENSITIVITY_META, STATUS_META } from '@/types/content';
import { PageHeader } from '@/components/patterns/page-header';
import { StatusBadge } from '@/components/patterns/status-badge';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
import { DynamicForm } from '@/components/content/dynamic-form';
import { Button } from '@/components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Separator } from '@/components/ui/separator';
import { IconArchive, IconHistory, IconRollback } from '@/components/icons';
import { format } from 'date-fns';

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

  const statusMeta = STATUS_META[content.status] ?? STATUS_META[ContentStatus.Draft];
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

  const title = String(Object.values(content.data).find((v) => typeof v === 'string' && v) ?? id);

  return (
    <>
      <PageHeader
        title={title}
        description={`${schema?.displayName ?? content.contentType} · ${sensitivityMeta?.label ?? 'Public'} · version ${content.version}`}
        actions={
          <div className="flex items-center gap-2">
            <StatusBadge tone={statusMeta.tone}>{statusMeta.label}</StatusBadge>
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

        <TabsContent value="history" className="mt-4">
          <HistoryPanel id={id} active={tab === 'history'} canRollback={canRollback} />
        </TabsContent>
      </Tabs>
    </>
  );
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

  return (
    <ol className="space-y-3">
      {versions.map((version, i) => (
        <li key={version.versionId} className="rounded-lg border p-4">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <p className="text-sm font-medium">
                {i === 0 ? 'Current version' : `Version from ${format(new Date(version.timestamp), 'PPp')}`}
              </p>
              <p className="text-muted-foreground text-xs">
                {version.lastModifiedBy ? `By ${version.lastModifiedBy} · ` : ''}
                {format(new Date(version.timestamp), 'PPpp')}
              </p>
            </div>
            {i > 0 && canRollback && (
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
          <pre className="bg-muted text-muted-foreground mt-3 max-h-48 overflow-auto rounded-md p-3 font-mono text-xs">
            {JSON.stringify(version.data, null, 2)}
          </pre>
        </li>
      ))}
    </ol>
  );
}
