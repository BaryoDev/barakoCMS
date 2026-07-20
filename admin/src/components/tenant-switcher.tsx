'use client';

import { useEffect, useRef } from 'react';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { useMyTenants, useCurrentTenant, useSwitchTenant } from '@/hooks/use-tenants';

/**
 * Tenant picker for multi-tenant deployments. Two behaviours:
 *  - Auto-scope: if the session token isn't scoped to a tenant the user belongs to (e.g. login lands
 *    on the default tenant), switch into their first tenant automatically — so a single-club user
 *    lands on their club's data without any UI.
 *  - Manual switch: when the user belongs to more than one tenant, show a picker to move between them.
 * Single-tenant deployments (no memberships) render nothing and never auto-switch.
 */
export function TenantSwitcher() {
  const { data: tenants } = useMyTenants();
  const current = useCurrentTenant();
  const switchTenant = useSwitchTenant();
  const autoSwitched = useRef(false);

  const belongsToCurrent = !!tenants?.some((t) => t.slug === current);

  useEffect(() => {
    if (autoSwitched.current || switchTenant.isPending) return;
    if (tenants && tenants.length > 0 && !belongsToCurrent) {
      autoSwitched.current = true;
      switchTenant.mutate(tenants[0].slug);
    }
  }, [tenants, belongsToCurrent, switchTenant]);

  if (!tenants || tenants.length <= 1) return null;

  const value = belongsToCurrent ? current ?? undefined : undefined;

  return (
    <Select
      value={value}
      onValueChange={(slug) => slug !== current && switchTenant.mutate(slug)}
      disabled={switchTenant.isPending}
    >
      <SelectTrigger size="sm" className="w-44" aria-label="Switch tenant">
        <SelectValue placeholder="Select tenant" />
      </SelectTrigger>
      <SelectContent>
        {tenants.map((t) => (
          <SelectItem key={t.slug} value={t.slug}>
            {t.name}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
