'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';

export type MembershipStatus = 'Active' | 'Suspended' | 'Removed';

export interface TenantMember {
  userId: string;
  username: string;
  email: string;
  roleIds: string[];
  status: MembershipStatus;
  joinedAt: string;
}

/** A role an administrator may hand out inside a tenant. SuperAdmin is never in this list: it is a
 *  platform role, and the server refuses it on both write paths. */
export interface AssignableRole {
  id: string;
  name: string;
  description: string;
}

export interface AddMemberInput {
  email: string;
  roleIds: string[];
}

export interface UpdateMemberInput {
  userId: string;
  roleIds: string[];
  status: Exclude<MembershipStatus, 'Removed'>;
}

const KEY = ['tenant', 'members'];

/** The roster for the tenant the current token is scoped to. The API derives the tenant from the
 *  token rather than from a route parameter, so there is nothing to pass here. */
export function useTenantMembers() {
  return useQuery({
    queryKey: KEY,
    queryFn: async () =>
      (await api.get<Paginated<TenantMember>>('/api/tenants/members')).data.items ?? [],
  });
}

export function useAssignableRoles() {
  return useQuery({
    queryKey: [...KEY, 'roles'],
    queryFn: async () =>
      (await api.get<Paginated<AssignableRole>>('/api/tenants/members/roles')).data.items ?? [],
    staleTime: 5 * 60 * 1000,
  });
}

/** Add by email. An address nobody holds yet creates an OTP-only account: no password, they sign in
 *  with an emailed code. Re-adding somebody who was removed reactivates their existing membership. */
export function useAddTenantMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: AddMemberInput) =>
      (await api.post<TenantMember>('/api/tenants/members', input)).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: KEY }),
  });
}

export function useUpdateTenantMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ userId, ...body }: UpdateMemberInput) =>
      (await api.put<TenantMember>(`/api/tenants/members/${userId}`, body)).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: KEY }),
  });
}

/** Marks the membership Removed. The row survives, so history and audit survive with it. */
export function useRemoveTenantMember() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (userId: string) => {
      await api.delete(`/api/tenants/members/${userId}`);
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: KEY }),
  });
}
