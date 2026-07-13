'use client';

import { Suspense, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { toast } from 'sonner';
import { useSchemas } from '@/hooks/use-schemas';
import { useCreateContent } from '@/hooks/use-contents';
import { apiErrorMessage } from '@/lib/api';
import { ContentStatus, SensitivityLevel, SENSITIVITY_META } from '@/types/content';
import { PageHeader } from '@/components/patterns/page-header';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { DynamicForm } from '@/components/content/dynamic-form';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Separator } from '@/components/ui/separator';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';

function NewContentInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { data: schemas, isLoading } = useSchemas();
  const createContent = useCreateContent();

  const [contentType, setContentType] = useState(searchParams.get('type') ?? '');
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [sensitivity, setSensitivity] = useState(SensitivityLevel.Public);

  const schema = schemas?.find((s) => s.name === contentType);

  const submit = (status: ContentStatus) => {
    createContent.mutate(
      { contentType, data: values, status, sensitivity },
      {
        onSuccess: ({ id }) => {
          toast.success(status === ContentStatus.Published ? 'Entry published' : 'Draft saved');
          router.push(`/content/${id}`);
        },
        onError: (error) => toast.error(apiErrorMessage(error, 'The entry could not be saved.')),
      }
    );
  };

  if (isLoading) return <TableSkeleton />;

  return (
    <>
      <PageHeader title="New entry" description="Pick a content type, fill in its fields, then save as a draft or publish." />

      <div className="max-w-2xl space-y-6">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="space-y-2">
            <Label>Content type</Label>
            <Select
              value={contentType}
              onValueChange={(v) => {
                setContentType(v);
                setValues({});
              }}
            >
              <SelectTrigger className="w-full">
                <SelectValue placeholder="Choose a type" />
              </SelectTrigger>
              <SelectContent>
                {schemas?.map((s) => (
                  <SelectItem key={s.name} value={s.name}>
                    {s.displayName}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-2">
            <Label>Visibility</Label>
            <Select
              value={String(sensitivity)}
              onValueChange={(v) => setSensitivity(Number(v) as SensitivityLevel)}
            >
              <SelectTrigger className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {Object.entries(SENSITIVITY_META).map(([value, meta]) => (
                  <SelectItem key={value} value={value}>
                    <span className="font-medium">{meta.label}</span>
                    <span className="text-muted-foreground ml-1.5 text-xs">{meta.description}</span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        </div>

        {schema && (
          <>
            <Separator />
            <DynamicForm fields={schema.fields} values={values} onChange={setValues} />
            <div className="flex items-center gap-2">
              <Button
                onClick={() => submit(ContentStatus.Published)}
                disabled={createContent.isPending}
              >
                Publish
              </Button>
              <Button
                variant="outline"
                onClick={() => submit(ContentStatus.Draft)}
                disabled={createContent.isPending}
              >
                Save as draft
              </Button>
              <Button variant="ghost" onClick={() => router.push('/content')}>
                Cancel
              </Button>
            </div>
          </>
        )}
      </div>
    </>
  );
}

export default function NewContentPage() {
  return (
    <Suspense fallback={<TableSkeleton />}>
      <NewContentInner />
    </Suspense>
  );
}
