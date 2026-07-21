'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useWorkflows } from '@/hooks/use-workflows';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconBolt, IconPlus, IconWorkflows } from '@/components/icons';

export default function WorkflowsPage() {
  const router = useRouter();
  const { data: workflows, isLoading, isError, refetch } = useWorkflows();

  return (
    <>
      <PageHeader
        title="Workflows"
        description="Automations that react to content events — send email, call webhooks, update fields, and more."
        actions={
          <Button asChild size="sm">
            <Link href="/workflows/new">
              <IconPlus />
              New workflow
            </Link>
          </Button>
        }
      />

      {isLoading ? (
        <TableSkeleton />
      ) : isError ? (
        <ErrorState entity="workflows" onRetry={() => refetch()} />
      ) : !workflows?.length ? (
        <EmptyState
          icon={IconWorkflows}
          title="No workflows yet"
          description="A workflow runs automatically when an entry of a chosen type is created or updated."
          action={
            <Button asChild size="sm">
              <Link href="/workflows/new">
                <IconPlus />
                New workflow
              </Link>
            </Button>
          }
        />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Workflow</TableHead>
                <TableHead>Trigger</TableHead>
                <TableHead className="hidden sm:table-cell">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {workflows.map((workflow) => (
                <TableRow
                  key={workflow.id}
                  className="cursor-pointer"
                  onClick={() => router.push(`/workflows/${workflow.id}`)}
                >
                  <TableCell className="font-medium">{workflow.name}</TableCell>
                  <TableCell>
                    <span className="flex items-center gap-1.5 text-sm">
                      <IconBolt className="text-primary size-3.5" />
                      <span className="font-mono text-xs">{workflow.triggerContentType}</span>
                      <span className="text-muted-foreground">· {workflow.triggerEvent}</span>
                    </span>
                  </TableCell>
                  <TableCell className="hidden sm:table-cell">
                    <div className="flex flex-wrap gap-1">
                      {workflow.actions.map((action, i) => (
                        <Badge key={i} variant="secondary" className="font-normal">
                          {action.type}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </>
  );
}
