'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useCreateSchema } from '@/hooks/use-schemas';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { FieldEditor } from '@/components/schema/field-editor';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { Separator } from '@/components/ui/separator';
import type { FieldDefinition } from '@/types/schema';

function slugify(input: string): string {
  return input.trim().toLowerCase().replace(/[^a-z0-9\s-]/g, '').replace(/\s+/g, '-');
}

export default function NewSchemaPage() {
  const router = useRouter();
  const createSchema = useCreateSchema();
  const [displayName, setDisplayName] = useState('');
  const [name, setName] = useState('');
  const [nameEdited, setNameEdited] = useState(false);
  const [description, setDescription] = useState('');
  const [fields, setFields] = useState<FieldDefinition[]>([]);

  const canSave = displayName.trim() && name.trim() && fields.length > 0;

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!canSave) return;
    createSchema.mutate(
      { name, displayName, description: description || undefined, fields },
      {
        onSuccess: () => {
          toast.success(`Content type “${displayName}” created`);
          router.push('/schemas');
        },
        onError: (error) => toast.error(apiErrorMessage(error, 'The content type could not be created.')),
      }
    );
  };

  return (
    <>
      <PageHeader
        title="New content type"
        description="Types cannot be edited or deleted through the API once created, so double-check the fields."
      />

      <form onSubmit={handleSubmit} className="max-w-2xl space-y-6">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="space-y-2">
            <Label htmlFor="displayName">Display name</Label>
            <Input
              id="displayName"
              value={displayName}
              placeholder="Blog post"
              required
              onChange={(e) => {
                setDisplayName(e.target.value);
                if (!nameEdited) setName(slugify(e.target.value));
              }}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="name">API name (slug)</Label>
            <Input
              id="name"
              value={name}
              placeholder="blog-post"
              required
              className="font-mono"
              onChange={(e) => {
                setNameEdited(true);
                setName(slugify(e.target.value));
              }}
            />
          </div>
        </div>

        <div className="space-y-2">
          <Label htmlFor="description">Description</Label>
          <Textarea
            id="description"
            value={description}
            rows={2}
            placeholder="What this type is for (optional)"
            onChange={(e) => setDescription(e.target.value)}
          />
        </div>

        <Separator />

        <FieldEditor fields={fields} onChange={setFields} />

        <div className="flex items-center gap-2">
          <Button type="submit" disabled={!canSave || createSchema.isPending}>
            {createSchema.isPending ? 'Creating…' : 'Create content type'}
          </Button>
          <Button type="button" variant="ghost" onClick={() => router.push('/schemas')}>
            Cancel
          </Button>
        </div>
      </form>
    </>
  );
}
