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

// Matches Tenant.DefaultSlug on the server. The default partition is the platform/home context; the
// cross-tenant token guard always permits a token for it, so switching here never needs a membership.
const DEFAULT_SLUG = 'default';

/**
 * Tenant picker for multi-tenant deployments. Behaviours:
 *  - Auto-scope: if the session token isn't scoped to a tenant the user belongs to (e.g. login lands
 *    on the default partition), switch into their first tenant automatically — so a single-tenant
 *    user lands on their data without any UI.
 *  - Manual switch: a user with any membership can move between their tenants AND back to Home (the
 *    default partition). Without the Home entry the default partition is unreachable once you've
 *    switched away — which is how a deployment's data got stranded under *DEFAULT*.
 * A user with no memberships (a plain single-tenant deployment) renders nothing and never auto-switches.
 */
export function TenantSwitcher() {
  const { data: memberships } = useMyTenants();
  const current = useCurrentTenant();
  const switchTenant = useSwitchTenant();
  const autoSwitched = useRef(false);

  // On first load, if the token isn't scoped to a tenant the user belongs to (login lands on the
  // default partition), auto-switch into their first tenant so they see their data immediately.
  // Only once per mount — a manual pick below sets the same ref so it never fights the user, which
  // is what lets a deliberate switch to Home stick.
  useEffect(() => {
    if (autoSwitched.current || switchTenant.isPending) return;
    const belongsToCurrent = !!memberships?.some((t) => t.slug === current);
    if (memberships && memberships.length > 0 && !belongsToCurrent) {
      autoSwitched.current = true;
      switchTenant.mutate(memberships[0].slug);
    }
  }, [memberships, current, switchTenant]);

  // Nothing to switch on a single-tenant deployment (no memberships).
  if (!memberships || memberships.length === 0) return null;

  // Home (default) is always offered alongside the user's tenants.
  const options = [{ slug: DEFAULT_SLUG, name: 'Home' }, ...memberships];
  const value = options.some((o) => o.slug === current) ? current ?? undefined : undefined;

  return (
    <Select
      value={value}
      onValueChange={(slug) => {
        // A manual pick settles the tenant — stop the auto-switch effect from overriding it, so a
        // deliberate switch to Home (default) isn't immediately bounced back to the user's club.
        autoSwitched.current = true;
        if (slug !== current) switchTenant.mutate(slug);
      }}
      disabled={switchTenant.isPending}
    >
      <SelectTrigger size="sm" className="w-44" aria-label="Switch tenant">
        <SelectValue placeholder="Select tenant" />
      </SelectTrigger>
      <SelectContent>
        {options.map((t) => (
          <SelectItem key={t.slug} value={t.slug}>
            {t.name}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
