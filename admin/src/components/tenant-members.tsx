'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  useTenantMembers,
  useAssignableRoles,
  useAddTenantMember,
  useUpdateTenantMember,
  useRemoveTenantMember,
  type TenantMember,
} from '@/hooks/use-tenant-members';
import { apiErrorMessage } from '@/lib/api';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconTrash, IconUserPlus, IconUsers } from '@/components/icons';

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function RoleCheckboxes({
  selected,
  onToggle,
  idPrefix,
}: {
  selected: string[];
  onToggle: (id: string, on: boolean) => void;
  idPrefix: string;
}) {
  const { data: roles } = useAssignableRoles();

  if (!roles?.length) {
    return (
      <p className="text-muted-foreground text-xs">
        No roles are assignable yet. A member with none can sign in but holds nothing in this tenant.
      </p>
    );
  }

  return (
    <div className="space-y-2">
      {roles.map((role) => (
        <div key={role.id} className="flex items-start gap-2">
          <Checkbox
            id={`${idPrefix}-${role.id}`}
            checked={selected.includes(role.id)}
            onCheckedChange={(v) => onToggle(role.id, v === true)}
          />
          <Label htmlFor={`${idPrefix}-${role.id}`} className="font-normal">
            {role.name}
            {role.description ? (
              <span className="text-muted-foreground block text-xs">{role.description}</span>
            ) : null}
          </Label>
        </div>
      ))}
    </div>
  );
}

function AddMemberDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const add = useAddTenantMember();
  const [email, setEmail] = useState('');
  const [roleIds, setRoleIds] = useState<string[]>([]);

  const emailValid = EMAIL_RE.test(email.trim());
  const canSave = emailValid && !add.isPending;

  function reset() {
    setEmail('');
    setRoleIds([]);
    add.reset();
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSave) return;
    try {
      await add.mutateAsync({ email: email.trim().toLowerCase(), roleIds });
      toast.success(`${email.trim()} added to this tenant`, {
        description: 'If they had no account, one was created and they sign in with an emailed code.',
      });
      reset();
      onOpenChange(false);
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not add the member.'));
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(v) => {
        if (!v) reset();
        onOpenChange(v);
      }}
    >
      <DialogContent>
        <form onSubmit={submit}>
          <DialogHeader>
            <DialogTitle>Add member</DialogTitle>
            <DialogDescription>
              Add somebody to this tenant by email. An address with no account yet gets one with no
              password, signing in with an emailed code.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 py-4">
            <div className="space-y-1.5">
              <Label htmlFor="member-email">Email</Label>
              <Input
                id="member-email"
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="person@example.com"
                aria-invalid={email.length > 0 && !emailValid}
                // eslint-disable-next-line jsx-a11y/no-autofocus -- focus belongs in a dialog the moment it opens, which is what WAI-ARIA authoring practices ask for. The rule is aimed at autofocus on page load.
                autoFocus
              />
            </div>

            <div className="space-y-1.5">
              <Label>Roles in this tenant</Label>
              <RoleCheckboxes
                idPrefix="add-role"
                selected={roleIds}
                onToggle={(id, on) =>
                  setRoleIds((prev) => (on ? [...prev, id] : prev.filter((r) => r !== id)))
                }
              />
            </div>
          </div>

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={!canSave}>
              {add.isPending ? 'Adding…' : 'Add member'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function EditMemberDialog({
  member,
  onOpenChange,
}: {
  member: TenantMember | null;
  onOpenChange: (v: boolean) => void;
}) {
  const update = useUpdateTenantMember();
  const [roleIds, setRoleIds] = useState<string[]>([]);
  const [suspended, setSuspended] = useState(false);
  const [loadedFor, setLoadedFor] = useState<string | null>(null);

  // Seed the form from the member being edited, once per member rather than on every render.
  if (member && loadedFor !== member.userId) {
    setLoadedFor(member.userId);
    setRoleIds(member.roleIds ?? []);
    setSuspended(member.status === 'Suspended');
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!member) return;
    try {
      await update.mutateAsync({
        userId: member.userId,
        roleIds,
        status: suspended ? 'Suspended' : 'Active',
      });
      toast.success(`${member.email || member.username} updated`);
      onOpenChange(false);
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not update the member.'));
    }
  }

  return (
    <Dialog open={member !== null} onOpenChange={onOpenChange}>
      <DialogContent>
        <form onSubmit={submit}>
          <DialogHeader>
            <DialogTitle>Edit member</DialogTitle>
            <DialogDescription>
              {member?.email || member?.username} in this tenant. Roles here apply to this tenant
              only.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 py-4">
            <div className="space-y-1.5">
              <Label>Roles in this tenant</Label>
              <RoleCheckboxes
                idPrefix="edit-role"
                selected={roleIds}
                onToggle={(id, on) =>
                  setRoleIds((prev) => (on ? [...prev, id] : prev.filter((r) => r !== id)))
                }
              />
            </div>

            <div className="flex items-start gap-2">
              <Checkbox
                id="member-suspended"
                checked={suspended}
                onCheckedChange={(v) => setSuspended(v === true)}
              />
              <Label htmlFor="member-suspended" className="font-normal">
                Suspended
                <span className="text-muted-foreground block text-xs">
                  Keeps them on the roster but stops this tenant issuing them a token.
                </span>
              </Label>
            </div>
          </div>

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={update.isPending}>
              {update.isPending ? 'Saving…' : 'Save changes'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The roster for the tenant the current session is scoped to. Not a route parameter: the API reads
 * the tenant from the token, the same way every other request here does.
 */
export function TenantMembers() {
  const { data: members, isLoading, isError, refetch } = useTenantMembers();
  const remove = useRemoveTenantMember();
  const [addOpen, setAddOpen] = useState(false);
  const [editing, setEditing] = useState<TenantMember | null>(null);

  const addButton = (
    <Button size="sm" variant="outline" onClick={() => setAddOpen(true)}>
      <IconUserPlus />
      Add member
    </Button>
  );

  async function removeMember(member: TenantMember) {
    try {
      await remove.mutateAsync(member.userId);
      toast.success(`${member.email || member.username} removed from this tenant`);
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not remove the member.'));
    }
  }

  return (
    <section className="mt-10 space-y-4">
      <div className="flex items-end justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold">Members of this tenant</h2>
          <p className="text-muted-foreground text-sm">
            Everyone who can sign in to the tenant this session is scoped to, and what they hold in it.
          </p>
        </div>
        {addButton}
      </div>

      {isLoading ? (
        <TableSkeleton />
      ) : isError ? (
        <ErrorState entity="members" onRetry={() => refetch()} />
      ) : !members?.length ? (
        <EmptyState
          icon={IconUsers}
          title="No members yet"
          description="Add somebody by email so they can sign in to this tenant."
          action={addButton}
        />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Email</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="w-px" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {members.map((m) => (
                <TableRow key={m.userId}>
                  <TableCell className="font-medium">{m.email || m.username}</TableCell>
                  <TableCell className="text-muted-foreground text-xs">
                    {m.roleIds?.length ?? 0} role{(m.roleIds?.length ?? 0) === 1 ? '' : 's'}
                  </TableCell>
                  <TableCell>
                    <Badge variant={m.status === 'Active' ? 'default' : 'secondary'}>{m.status}</Badge>
                  </TableCell>
                  <TableCell className="text-right whitespace-nowrap">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(m)}>
                      Edit
                    </Button>
                    <ConfirmDialog
                      trigger={
                        <Button size="sm" variant="ghost" aria-label={`Remove ${m.email || m.username}`}>
                          <IconTrash />
                        </Button>
                      }
                      title="Remove this member?"
                      description={`${m.email || m.username} loses access to this tenant. The record is kept, so they can be added back later.`}
                      confirmLabel="Remove"
                      destructive
                      onConfirm={() => void removeMember(m)}
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <AddMemberDialog open={addOpen} onOpenChange={setAddOpen} />
      <EditMemberDialog member={editing} onOpenChange={(v) => !v && setEditing(null)} />
    </section>
  );
}
