'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  useAssignGroup,
  useAssignRole,
  useRemoveGroup,
  useRemoveRole,
  useRoles,
  useUsers,
} from '@/hooks/use-rbac';
import { useUserGroups } from '@/hooks/use-user-groups';
import { apiErrorMessage } from '@/lib/api';
import type { User } from '@/types/rbac';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { PaginationControls } from '@/components/patterns/pagination-controls';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconPlus, IconTimes, IconUsers } from '@/components/icons';
import { format } from 'date-fns';

export default function UsersPage() {
  const [page, setPage] = useState(1);
  const { data: users, isLoading } = useUsers({ page });
  const { data: roles } = useRoles({ pageSize: 100 });
  const { data: groups } = useUserGroups();

  const roleName = (id: string) => roles?.items.find((r) => r.id === id)?.name ?? '…';
  const groupName = (id: string) => groups?.find((g) => g.id === id)?.name ?? '…';

  return (
    <>
      <PageHeader
        title="Users"
        description="Everyone with an account. Users sign up through the API; here you control what they can do."
      />

      {isLoading ? (
        <TableSkeleton />
      ) : !users?.items.length ? (
        <EmptyState
          icon={IconUsers}
          title="No users yet"
          description="Accounts are created through the registration endpoint. New users start with the User role."
        />
      ) : (
        <>
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>User</TableHead>
                  <TableHead>Roles</TableHead>
                  <TableHead className="hidden lg:table-cell">Groups</TableHead>
                  <TableHead className="hidden text-right sm:table-cell">Joined</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {users.items.map((user) => (
                  <TableRow key={user.id}>
                    <TableCell>
                      <div className="font-medium">{user.username}</div>
                      <div className="text-muted-foreground text-xs">{user.email}</div>
                    </TableCell>
                    <TableCell>
                      <MembershipCell
                        user={user}
                        assigned={user.roleIds}
                        nameOf={roleName}
                        options={roles?.items.map((r) => ({ id: r.id, name: r.name })) ?? []}
                        kind="role"
                      />
                    </TableCell>
                    <TableCell className="hidden lg:table-cell">
                      <MembershipCell
                        user={user}
                        assigned={user.groupIds}
                        nameOf={groupName}
                        options={groups?.map((g) => ({ id: g.id, name: g.name })) ?? []}
                        kind="group"
                      />
                    </TableCell>
                    <TableCell className="text-muted-foreground hidden text-right text-sm sm:table-cell">
                      {format(new Date(user.createdAt), 'PP')}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          <PaginationControls page={users} onPageChange={setPage} />
        </>
      )}
    </>
  );
}

function MembershipCell({
  user,
  assigned,
  nameOf,
  options,
  kind,
}: {
  user: User;
  assigned: string[];
  nameOf: (id: string) => string;
  options: { id: string; name: string }[];
  kind: 'role' | 'group';
}) {
  const assignRole = useAssignRole();
  const removeRole = useRemoveRole();
  const assignGroup = useAssignGroup();
  const removeGroup = useRemoveGroup();

  const available = options.filter((o) => !assigned.includes(o.id));

  const onError = (error: unknown) =>
    toast.error(apiErrorMessage(error, `The ${kind} could not be changed.`));

  const add = (id: string) => {
    if (kind === 'role') assignRole.mutate({ userId: user.id, roleId: id }, { onError });
    else assignGroup.mutate({ userId: user.id, groupId: id }, { onError });
  };

  const remove = (id: string) => {
    if (kind === 'role') removeRole.mutate({ userId: user.id, roleId: id }, { onError });
    else removeGroup.mutate({ userId: user.id, groupId: id }, { onError });
  };

  return (
    <div className="flex flex-wrap items-center gap-1">
      {assigned.map((id) => (
        <Badge key={id} variant="secondary" className="gap-1 font-normal">
          {nameOf(id)}
          <button
            type="button"
            aria-label={`Remove ${kind} ${nameOf(id)} from ${user.username}`}
            onClick={() => remove(id)}
          >
            <IconTimes className="size-3" />
          </button>
        </Badge>
      ))}
      {available.length > 0 && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              variant="ghost"
              size="icon"
              className="size-6"
              aria-label={`Add ${kind} to ${user.username}`}
            >
              <IconPlus className="size-3" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start">
            <DropdownMenuLabel>Add {kind}</DropdownMenuLabel>
            {available.map((option) => (
              <DropdownMenuItem key={option.id} onClick={() => add(option.id)}>
                {option.name}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
    </div>
  );
}
