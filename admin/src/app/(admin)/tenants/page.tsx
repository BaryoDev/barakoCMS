'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { useTenants, useCreateTenant } from '@/hooks/use-tenants';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
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
import { IconPlus, IconServer } from '@/components/icons';

// Handle rules mirror the server (TenantHandles): 3-40 chars, lowercase alphanumerics + hyphens,
// no leading/trailing hyphen. Validated inline so the user sees the rule before submitting.
const HANDLE_RE = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;

function CreateTenantDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const create = useCreateTenant();
  const [name, setName] = useState('');
  const [handle, setHandle] = useState('');
  const [handleEdited, setHandleEdited] = useState(false);
  const [about, setAbout] = useState('');
  const [isActive, setIsActive] = useState(true);

  // Derive the handle from the name until the user edits it directly (same nicety as the schema form).
  const derivedHandle = handleEdited
    ? handle
    : name
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 40);

  const handleValid = HANDLE_RE.test(derivedHandle);
  const canSave = name.trim().length > 0 && handleValid && !create.isPending;

  function reset() {
    setName('');
    setHandle('');
    setHandleEdited(false);
    setAbout('');
    setIsActive(true);
    create.reset();
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSave) return;
    try {
      await create.mutateAsync({
        Handle: derivedHandle,
        Name: name.trim(),
        About: about.trim() || undefined,
        IsActive: isActive,
      });
      toast.success(`Tenant "${name.trim()}" created`, {
        description: 'You were added as an admin member, so you can switch to it right away.',
      });
      reset();
      onOpenChange(false);
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not create the tenant.'));
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
            <DialogTitle>New tenant</DialogTitle>
            <DialogDescription>
              A tenant is an isolated space with its own content, users and data. You&apos;ll be added
              as its first admin.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 py-4">
            <div className="space-y-1.5">
              <Label htmlFor="tenant-name">Name</Label>
              <Input
                id="tenant-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Acme Corporation"
                autoFocus
              />
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="tenant-handle">Handle</Label>
              <Input
                id="tenant-handle"
                value={derivedHandle}
                onChange={(e) => {
                  setHandleEdited(true);
                  setHandle(e.target.value.toLowerCase());
                }}
                placeholder="acme"
                aria-invalid={derivedHandle.length > 0 && !handleValid}
              />
              <p className="text-muted-foreground text-xs">
                {derivedHandle.length > 0 && !handleValid
                  ? '3–40 characters, lowercase letters, numbers and hyphens; no leading or trailing hyphen.'
                  : 'Used in URLs and the X-Tenant header. Cannot be changed later.'}
              </p>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="tenant-about">About (optional)</Label>
              <Input
                id="tenant-about"
                value={about}
                onChange={(e) => setAbout(e.target.value)}
                placeholder="Short description"
              />
            </div>

            <div className="flex items-center justify-between">
              <div>
                <Label htmlFor="tenant-active">Active</Label>
                <p className="text-muted-foreground text-xs">Inactive tenants can&apos;t issue tokens.</p>
              </div>
              <Switch id="tenant-active" checked={isActive} onCheckedChange={setIsActive} />
            </div>
          </div>

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={!canSave}>
              {create.isPending ? 'Creating…' : 'Create tenant'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export default function TenantsPage() {
  const { data: tenants, isLoading, isError, refetch } = useTenants();
  const [dialogOpen, setDialogOpen] = useState(false);

  const newButton = (
    <Button size="sm" onClick={() => setDialogOpen(true)}>
      <IconPlus />
      New tenant
    </Button>
  );

  return (
    <>
      <PageHeader
        title="Tenants"
        description="Isolated spaces on this deployment — each with its own content, users and data."
        actions={newButton}
      />

      {isLoading ? (
        <TableSkeleton />
      ) : isError ? (
        <ErrorState entity="tenants" onRetry={() => refetch()} />
      ) : !tenants?.length ? (
        <EmptyState
          icon={IconServer}
          title="No tenants yet"
          description="Create a tenant to run more than one isolated space on this deployment."
          action={newButton}
        />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Handle</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tenants.map((t) => (
                <TableRow key={t.id}>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell className="text-muted-foreground font-mono text-xs">{t.slug}</TableCell>
                  <TableCell>
                    <Badge variant={t.isActive ? 'default' : 'secondary'}>
                      {t.isActive ? 'Active' : 'Inactive'}
                    </Badge>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <CreateTenantDialog open={dialogOpen} onOpenChange={setDialogOpen} />
    </>
  );
}
