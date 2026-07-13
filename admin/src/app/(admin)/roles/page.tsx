'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useRoles, useDeleteRole } from '@/hooks/use-rbac';
import { apiErrorMessage } from '@/lib/api';
import { SYSTEM_ROLE_NAMES } from '@/types/rbac';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { PaginationControls } from '@/components/patterns/pagination-controls';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
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
import { IconPlus, IconRoles, IconTrash } from '@/components/icons';

export default function RolesPage() {
  const router = useRouter();
  const [page, setPage] = useState(1);
  const { data: roles, isLoading } = useRoles({ page });
  const deleteRole = useDeleteRole();

  return (
    <>
      <PageHeader
        title="Roles"
        description="What each kind of user is allowed to do, per content type."
        actions={
          <Button asChild size="sm">
            <Link href="/roles/new">
              <IconPlus />
              New role
            </Link>
          </Button>
        }
      />

      {isLoading ? (
        <TableSkeleton />
      ) : !roles?.items.length ? (
        <EmptyState
          icon={IconRoles}
          title="No roles yet"
          description="Roles bundle content permissions. Assign them to users on the Users page."
        />
      ) : (
        <>
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Role</TableHead>
                  <TableHead className="hidden sm:table-cell">Description</TableHead>
                  <TableHead>Permissions</TableHead>
                  <TableHead className="w-12" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {roles.items.map((role) => {
                  const isSystem = SYSTEM_ROLE_NAMES.includes(role.name);
                  return (
                    <TableRow
                      key={role.id}
                      className="cursor-pointer"
                      onClick={() => router.push(`/roles/${role.id}`)}
                    >
                      <TableCell>
                        <span className="font-medium">{role.name}</span>
                        {isSystem && (
                          <Badge variant="secondary" className="ml-2 text-xs">
                            System
                          </Badge>
                        )}
                      </TableCell>
                      <TableCell className="text-muted-foreground hidden max-w-xs truncate text-sm sm:table-cell">
                        {role.description || '—'}
                      </TableCell>
                      <TableCell className="text-muted-foreground text-sm">
                        {role.name === 'SuperAdmin'
                          ? 'Everything'
                          : `${role.permissions?.length ?? 0} content ${
                              (role.permissions?.length ?? 0) === 1 ? 'type' : 'types'
                            }`}
                      </TableCell>
                      <TableCell onClick={(e) => e.stopPropagation()}>
                        {!isSystem && (
                          <ConfirmDialog
                            trigger={
                              <Button
                                variant="ghost"
                                size="icon"
                                aria-label={`Delete ${role.name}`}
                                className="text-destructive hover:text-destructive"
                              >
                                <IconTrash className="size-3.5" />
                              </Button>
                            }
                            title={`Delete the ${role.name} role?`}
                            description="Users still holding this role block deletion — unassign it first. This cannot be undone."
                            confirmLabel="Delete role"
                            destructive
                            onConfirm={() =>
                              deleteRole.mutate(role.id, {
                                onSuccess: () => toast.success(`Role “${role.name}” deleted`),
                                onError: (error) =>
                                  toast.error(apiErrorMessage(error, 'The role could not be deleted.')),
                              })
                            }
                          />
                        )}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          <PaginationControls page={roles} onPageChange={setPage} />
        </>
      )}
    </>
  );
}
