'use client';

import { useState } from 'react';
import { useSchemas } from '@/hooks/use-schemas';
import type { ContentTypePermission, PermissionAction, RoleRequest } from '@/types/rbac';
import { emptyPermission } from '@/types/rbac';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { Checkbox } from '@/components/ui/checkbox';
import { Badge } from '@/components/ui/badge';
import { Separator } from '@/components/ui/separator';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconTimes } from '@/components/icons';

const ACTIONS: PermissionAction[] = ['create', 'read', 'update', 'delete'];

interface RoleEditorProps {
  initial?: RoleRequest;
  submitLabel: string;
  isPending: boolean;
  onSubmit: (role: RoleRequest) => void;
  onCancel: () => void;
}

export function RoleEditor({ initial, submitLabel, isPending, onSubmit, onCancel }: RoleEditorProps) {
  const { data: schemas } = useSchemas();
  const [name, setName] = useState(initial?.name ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [permissions, setPermissions] = useState<ContentTypePermission[]>(initial?.permissions ?? []);
  const [capabilities, setCapabilities] = useState<string[]>(initial?.systemCapabilities ?? []);
  const [capabilityDraft, setCapabilityDraft] = useState('');

  const permissionFor = (slug: string) =>
    permissions.find((p) => p.contentTypeSlug === slug) ?? emptyPermission(slug);

  const toggle = (slug: string, action: PermissionAction, enabled: boolean) => {
    setPermissions((prev) => {
      const existing = prev.find((p) => p.contentTypeSlug === slug);
      const base = existing ?? emptyPermission(slug);
      const updated = { ...base, [action]: { ...base[action], enabled } };
      const rest = prev.filter((p) => p.contentTypeSlug !== slug);
      const isAllOff = ACTIONS.every((a) => !updated[a].enabled);
      return isAllOff ? rest : [...rest, updated];
    });
  };

  const addCapability = () => {
    const value = capabilityDraft.trim();
    if (value && !capabilities.includes(value)) {
      setCapabilities((prev) => [...prev, value]);
    }
    setCapabilityDraft('');
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    onSubmit({
      name: name.trim(),
      description: description.trim() || undefined,
      permissions,
      systemCapabilities: capabilities,
    });
  };

  return (
    <form onSubmit={handleSubmit} className="max-w-2xl space-y-6">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="space-y-2">
          <Label htmlFor="role-name">Name</Label>
          <Input
            id="role-name"
            value={name}
            placeholder="Editor"
            required
            onChange={(e) => setName(e.target.value)}
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="role-description">Description</Label>
          <Textarea
            id="role-description"
            value={description}
            rows={1}
            placeholder="What this role is for (optional)"
            onChange={(e) => setDescription(e.target.value)}
          />
        </div>
      </div>

      <Separator />

      <div className="space-y-2">
        <h3 className="text-sm font-medium">Content permissions</h3>
        <p className="text-muted-foreground text-xs">
          Grants are additive across a user&apos;s roles — any role that allows an action allows it.
        </p>
        {!schemas?.length ? (
          <p className="text-muted-foreground rounded-lg border border-dashed px-4 py-6 text-center text-sm">
            No content types exist yet, so there is nothing to grant. Create a content type first.
          </p>
        ) : (
          <div className="rounded-lg border">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Content type</TableHead>
                  {ACTIONS.map((action) => (
                    <TableHead key={action} className="w-20 text-center capitalize">
                      {action}
                    </TableHead>
                  ))}
                </TableRow>
              </TableHeader>
              <TableBody>
                {schemas.map((schema) => {
                  const permission = permissionFor(schema.name);
                  return (
                    <TableRow key={schema.name}>
                      <TableCell>
                        <span className="font-medium">{schema.displayName}</span>
                        <span className="text-muted-foreground ml-2 font-mono text-xs">{schema.name}</span>
                      </TableCell>
                      {ACTIONS.map((action) => (
                        <TableCell key={action} className="text-center">
                          <Checkbox
                            checked={permission[action].enabled}
                            onCheckedChange={(checked) => toggle(schema.name, action, checked === true)}
                            aria-label={`Allow ${action} on ${schema.displayName}`}
                          />
                        </TableCell>
                      ))}
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        )}
      </div>

      <Separator />

      <div className="space-y-2">
        <Label htmlFor="capability">System capabilities</Label>
        <p className="text-muted-foreground text-xs">
          Free-form capability tags, e.g. manage_users. Press Enter to add.
        </p>
        <div className="flex gap-2">
          <Input
            id="capability"
            value={capabilityDraft}
            placeholder="manage_users"
            className="max-w-xs font-mono text-xs"
            onChange={(e) => setCapabilityDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                addCapability();
              }
            }}
          />
          <Button type="button" variant="outline" size="sm" onClick={addCapability}>
            Add
          </Button>
        </div>
        {capabilities.length > 0 && (
          <div className="flex flex-wrap gap-1.5 pt-1">
            {capabilities.map((capability) => (
              <Badge key={capability} variant="secondary" className="gap-1 font-mono font-normal">
                {capability}
                <button
                  type="button"
                  aria-label={`Remove ${capability}`}
                  onClick={() => setCapabilities((prev) => prev.filter((c) => c !== capability))}
                >
                  <IconTimes className="size-3" />
                </button>
              </Badge>
            ))}
          </div>
        )}
      </div>

      <div className="flex items-center gap-2">
        <Button type="submit" disabled={!name.trim() || isPending}>
          {isPending ? 'Saving…' : submitLabel}
        </Button>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}
