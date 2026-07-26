'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  useApiKeys,
  useCreateApiKey,
  useRevokeApiKey,
  API_KEY_SCOPES,
  type CreatedApiKey,
} from '@/hooks/use-api-keys';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
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
import { IconPlus, IconKey, IconTrash } from '@/components/icons';

function formatDate(value?: string | null) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function CreateApiKeyDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const create = useCreateApiKey();
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<string[]>(['content:read']);
  const [expiresAt, setExpiresAt] = useState('');
  // Once created, hold the secret so it can be shown ONCE. Cleared on close.
  const [created, setCreated] = useState<CreatedApiKey | null>(null);

  const canSave = name.trim().length > 0 && scopes.length > 0 && !create.isPending;

  function reset() {
    setName('');
    setScopes(['content:read']);
    setExpiresAt('');
    setCreated(null);
    create.reset();
  }

  function toggleScope(value: string, checked: boolean) {
    setScopes((prev) => (checked ? [...new Set([...prev, value])] : prev.filter((s) => s !== value)));
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!canSave) return;
    try {
      const result = await create.mutateAsync({
        name: name.trim(),
        scopes,
        expiresAt: expiresAt ? new Date(expiresAt).toISOString() : undefined,
      });
      setCreated(result); // switch the dialog to the copy-once view
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not create the key.'));
    }
  }

  async function copyKey() {
    if (!created) return;
    await navigator.clipboard.writeText(created.key);
    toast.success('Key copied to clipboard');
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
        {created ? (
          <>
            <DialogHeader>
              <DialogTitle>Copy your API key</DialogTitle>
              <DialogDescription>
                This is the only time the full key is shown. Store it somewhere safe — you can&apos;t
                see it again.
              </DialogDescription>
            </DialogHeader>
            <div className="space-y-3 py-2">
              <div className="flex items-center gap-2">
                <Input readOnly value={created.key} className="font-mono text-xs" data-testid="api-key-secret" />
                <Button type="button" variant="outline" onClick={copyKey}>
                  Copy
                </Button>
              </div>
              <p className="text-muted-foreground text-xs">
                Send it as <code className="font-mono">Authorization: Bearer {created.prefix}…</code>
              </p>
            </div>
            <DialogFooter>
              <Button
                type="button"
                onClick={() => {
                  onOpenChange(false);
                }}
              >
                Done
              </Button>
            </DialogFooter>
          </>
        ) : (
          <form onSubmit={submit}>
            <DialogHeader>
              <DialogTitle>New API key</DialogTitle>
              <DialogDescription>
                For machine callers (SDKs, CI, integrations). Scoped to this tenant and the content
                API only.
              </DialogDescription>
            </DialogHeader>

            <div className="space-y-4 py-4">
              <div className="space-y-1.5">
                <Label htmlFor="key-name">Name</Label>
                <Input
                  id="key-name"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  placeholder="CI deploy"
                  autoFocus
                />
              </div>

              <div className="space-y-2">
                <Label>Scopes</Label>
                <div className="space-y-2 rounded-lg border p-3">
                  {API_KEY_SCOPES.map((s) => (
                    <label key={s.value} htmlFor={`scope-${s.value}`} className="flex items-start gap-2.5">
                      <Checkbox
                        id={`scope-${s.value}`}
                        checked={scopes.includes(s.value)}
                        onCheckedChange={(c) => toggleScope(s.value, c === true)}
                      />
                      <span className="text-sm leading-tight">
                        <span className="font-medium">{s.label}</span>
                        <span className="text-muted-foreground ml-1.5 text-xs">{s.description}</span>
                      </span>
                    </label>
                  ))}
                </div>
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="key-expiry">Expires (optional)</Label>
                <Input
                  id="key-expiry"
                  type="date"
                  value={expiresAt}
                  onChange={(e) => setExpiresAt(e.target.value)}
                  className="w-fit"
                />
              </div>
            </div>

            <DialogFooter>
              <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={!canSave}>
                {create.isPending ? 'Creating…' : 'Create key'}
              </Button>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

export default function ApiKeysPage() {
  const { data: keys, isLoading, isError, refetch } = useApiKeys();
  const revoke = useRevokeApiKey();
  const [dialogOpen, setDialogOpen] = useState(false);

  async function onRevoke(id: string, name: string) {
    if (!window.confirm(`Revoke "${name}"? Callers using it will stop working immediately.`)) return;
    try {
      await revoke.mutateAsync(id);
      toast.success(`Revoked "${name}"`);
    } catch (err) {
      toast.error(apiErrorMessage(err, 'Could not revoke the key.'));
    }
  }

  const newButton = (
    <Button size="sm" onClick={() => setDialogOpen(true)}>
      <IconPlus />
      New key
    </Button>
  );

  return (
    <>
      <PageHeader
        title="API keys"
        description="Long-lived keys for machine callers — SDKs, CI, integrations — scoped to the content API."
        actions={newButton}
      />

      {isLoading ? (
        <TableSkeleton />
      ) : isError ? (
        <ErrorState entity="API keys" onRetry={() => refetch()} />
      ) : !keys?.length ? (
        <EmptyState
          icon={IconKey}
          title="No API keys yet"
          description="Create a key so a machine caller can authenticate without a human's password."
          action={newButton}
        />
      ) : (
        <div className="rounded-lg border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Key</TableHead>
                <TableHead>Scopes</TableHead>
                <TableHead>Last used</TableHead>
                <TableHead>Expires</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="w-10" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {keys.map((k) => (
                <TableRow key={k.id}>
                  <TableCell className="font-medium">{k.name}</TableCell>
                  <TableCell className="text-muted-foreground font-mono text-xs">{k.prefix}…</TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {k.scopes.map((s) => (
                        <Badge key={s} variant="secondary" className="text-xs">
                          {s}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
                  <TableCell className="text-muted-foreground text-xs">{formatDate(k.lastUsedAt)}</TableCell>
                  <TableCell className="text-muted-foreground text-xs">{formatDate(k.expiresAt)}</TableCell>
                  <TableCell>
                    <Badge variant={k.revoked ? 'secondary' : 'default'}>
                      {k.revoked ? 'Revoked' : 'Active'}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    {!k.revoked && (
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        onClick={() => onRevoke(k.id, k.name)}
                        aria-label={`Revoke ${k.name}`}
                        className="text-destructive hover:text-destructive"
                      >
                        <IconTrash className="size-3.5" />
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <CreateApiKeyDialog open={dialogOpen} onOpenChange={setDialogOpen} />
    </>
  );
}
