'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  useAddGroupMember,
  useCreateUserGroup,
  useDeleteUserGroup,
  useRemoveGroupMember,
  useUpdateUserGroup,
  useUserGroups,
} from '@/hooks/use-user-groups';
import { useUsers } from '@/hooks/use-rbac';
import { apiErrorMessage } from '@/lib/api';
import type { UserGroup } from '@/types/rbac';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { IconGroups, IconPen, IconPlus, IconTimes, IconTrash } from '@/components/icons';

export default function UserGroupsPage() {
  const { data: groups, isLoading } = useUserGroups();
  const { data: users } = useUsers({ pageSize: 100 });
  const createGroup = useCreateUserGroup();
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editing, setEditing] = useState<UserGroup | null>(null);

  const newGroupButton = (
    <Button
      size="sm"
      onClick={() => {
        setEditing(null);
        setDialogOpen(true);
      }}
    >
      <IconPlus />
      New group
    </Button>
  );

  return (
    <>
      <PageHeader
        title="Groups"
        description="Organize users into teams. A group with members cannot be deleted until they are removed."
        actions={newGroupButton}
      />

      {isLoading ? (
        <TableSkeleton />
      ) : !groups?.length ? (
        <EmptyState
          icon={IconGroups}
          title="No groups yet"
          description="Groups collect users into teams like Marketing or Engineering, so membership is easy to see and manage."
          action={newGroupButton}
        />
      ) : (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {groups.map((group) => (
            <GroupCard
              key={group.id}
              group={group}
              users={users?.items ?? []}
              onEdit={() => {
                setEditing(group);
                setDialogOpen(true);
              }}
            />
          ))}
        </div>
      )}

      <GroupDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        group={editing}
        onSave={(name, description) => {
          if (editing) return;
          createGroup.mutate(
            { name, description: description || undefined },
            {
              onSuccess: () => {
                toast.success(`Group “${name}” created`);
                setDialogOpen(false);
              },
              onError: (error) => toast.error(apiErrorMessage(error, 'The group could not be created.')),
            }
          );
        }}
      />
    </>
  );
}

function GroupCard({
  group,
  users,
  onEdit,
}: {
  group: UserGroup;
  users: { id: string; username: string }[];
  onEdit: () => void;
}) {
  const deleteGroup = useDeleteUserGroup();
  const addMember = useAddGroupMember();
  const removeMember = useRemoveGroupMember();

  const memberName = (id: string) => users.find((u) => u.id === id)?.username ?? '…';
  const nonMembers = users.filter((u) => !group.userIds.includes(u.id));

  const onError = (error: unknown) =>
    toast.error(apiErrorMessage(error, 'The membership could not be changed.'));

  return (
    <div className="rounded-lg border p-4">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="flex items-center gap-2 text-sm font-medium">
            <IconGroups className="text-primary size-4 shrink-0" />
            <span className="truncate">{group.name}</span>
          </h3>
          {group.description && (
            <p className="text-muted-foreground mt-1 text-xs">{group.description}</p>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <Button variant="ghost" size="icon" aria-label={`Edit ${group.name}`} onClick={onEdit}>
            <IconPen className="size-3.5" />
          </Button>
          <ConfirmDialog
            trigger={
              <Button
                variant="ghost"
                size="icon"
                aria-label={`Delete ${group.name}`}
                className="text-destructive hover:text-destructive"
              >
                <IconTrash className="size-3.5" />
              </Button>
            }
            title={`Delete the ${group.name} group?`}
            description={
              group.userIds.length > 0
                ? 'This group still has members, so the API will refuse — remove the members first.'
                : 'This cannot be undone.'
            }
            confirmLabel="Delete group"
            destructive
            onConfirm={() =>
              deleteGroup.mutate(group.id, {
                onSuccess: () => toast.success(`Group “${group.name}” deleted`),
                onError: (error) => toast.error(apiErrorMessage(error, 'The group could not be deleted.')),
              })
            }
          />
        </div>
      </div>

      <div className="mt-3 flex flex-wrap items-center gap-1">
        {group.userIds.length === 0 && (
          <span className="text-muted-foreground text-xs">No members yet</span>
        )}
        {group.userIds.map((userId) => (
          <Badge key={userId} variant="secondary" className="gap-1 font-normal">
            {memberName(userId)}
            <button
              type="button"
              aria-label={`Remove ${memberName(userId)} from ${group.name}`}
              onClick={() => removeMember.mutate({ groupId: group.id, userId }, { onError })}
            >
              <IconTimes className="size-3" />
            </button>
          </Badge>
        ))}
        {nonMembers.length > 0 && (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="icon"
                className="size-6"
                aria-label={`Add a member to ${group.name}`}
              >
                <IconPlus className="size-3" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              <DropdownMenuLabel>Add member</DropdownMenuLabel>
              {nonMembers.map((user) => (
                <DropdownMenuItem
                  key={user.id}
                  onClick={() => addMember.mutate({ groupId: group.id, userId: user.id }, { onError })}
                >
                  {user.username}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        )}
      </div>
    </div>
  );
}

function GroupDialog({
  open,
  onOpenChange,
  group,
  onSave,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  group: UserGroup | null;
  onSave: (name: string, description: string) => void;
}) {
  const updateGroup = useUpdateUserGroup();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');

  // Re-seed the form each time the dialog opens for a different target.
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const target = group?.id ?? 'new';
  if (open && seededFor !== target) {
    setSeededFor(target);
    setName(group?.name ?? '');
    setDescription(group?.description ?? '');
  }
  if (!open && seededFor !== null) setSeededFor(null);

  const save = () => {
    if (!name.trim()) return;
    if (group) {
      updateGroup.mutate(
        { id: group.id, data: { name: name.trim(), description: description.trim() || undefined } },
        {
          onSuccess: () => {
            toast.success('Group saved');
            onOpenChange(false);
          },
          onError: (error) => toast.error(apiErrorMessage(error, 'The group could not be saved.')),
        }
      );
    } else {
      onSave(name.trim(), description.trim());
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{group ? 'Edit group' : 'New group'}</DialogTitle>
          <DialogDescription>
            {group ? 'Rename the group or update its description.' : 'Name the team this group represents.'}
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4 py-2">
          <div className="space-y-2">
            <Label htmlFor="group-name">Name</Label>
            <Input
              id="group-name"
              value={name}
              placeholder="Marketing"
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="group-description">Description</Label>
            <Input
              id="group-description"
              value={description}
              placeholder="What this group is for (optional)"
              onChange={(e) => setDescription(e.target.value)}
            />
          </div>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={save} disabled={!name.trim()}>
            {group ? 'Save group' : 'Create group'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
