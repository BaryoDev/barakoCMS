'use client';

import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useCreateRole } from '@/hooks/use-rbac';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { RoleEditor } from '@/components/rbac/role-editor';

export default function NewRolePage() {
  const router = useRouter();
  const createRole = useCreateRole();

  return (
    <>
      <PageHeader
        title="New role"
        description="Grant per-content-type permissions, then assign the role to users."
      />
      <RoleEditor
        submitLabel="Create role"
        isPending={createRole.isPending}
        onCancel={() => router.push('/roles')}
        onSubmit={(role) =>
          createRole.mutate(role, {
            onSuccess: () => {
              toast.success(`Role “${role.name}” created`);
              router.push('/roles');
            },
            onError: (error) => toast.error(apiErrorMessage(error, 'The role could not be created.')),
          })
        }
      />
    </>
  );
}
