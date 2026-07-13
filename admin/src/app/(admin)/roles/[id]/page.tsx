'use client';

import { use } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useRole, useUpdateRole } from '@/hooks/use-rbac';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { EmptyState } from '@/components/patterns/empty-state';
import { RoleEditor } from '@/components/rbac/role-editor';
import { Button } from '@/components/ui/button';
import { IconRoles } from '@/components/icons';

export default function EditRolePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const router = useRouter();
  const { data: role, isLoading } = useRole(id);
  const updateRole = useUpdateRole();

  if (isLoading) return <TableSkeleton />;

  if (!role) {
    return (
      <EmptyState
        icon={IconRoles}
        title="Role not found"
        description="This role does not exist anymore."
        action={
          <Button asChild variant="outline" size="sm">
            <Link href="/roles">Back to roles</Link>
          </Button>
        }
      />
    );
  }

  return (
    <>
      <PageHeader
        title={role.name}
        description="Changes apply to every user holding this role as soon as you save."
      />
      <RoleEditor
        initial={{
          name: role.name,
          description: role.description,
          permissions: role.permissions ?? [],
          systemCapabilities: role.systemCapabilities ?? [],
        }}
        submitLabel="Save role"
        isPending={updateRole.isPending}
        onCancel={() => router.push('/roles')}
        onSubmit={(data) =>
          updateRole.mutate(
            { id, data },
            {
              onSuccess: () => {
                toast.success('Role saved');
                router.push('/roles');
              },
              onError: (error) => toast.error(apiErrorMessage(error, 'The role could not be saved.')),
            }
          )
        }
      />
    </>
  );
}
