import type { ComponentType, SVGProps } from 'react';
import {
  IconDashboard,
  IconContentTypes,
  IconContent,
  IconWorkflows,
  IconUsers,
  IconRoles,
  IconGroups,
  IconServer,
  IconKey,
  IconHealth,
  IconAnalytics,
  IconCoins,
  IconFlag,
  IconMobile,
  IconBug,
  IconHistory,
  IconEnvelope,
  IconSettings,
  IconShield,
} from '@/components/icons';

export interface NavItem {
  title: string;
  href: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  /**
   * Roles the API actually accepts for this destination, copied from the `Roles(...)` call on the
   * endpoint behind it. Omitted means every signed-in user may see it.
   *
   * This is a copy, not a derivation, so it can drift. It is worth having anyway: showing someone
   * nineteen destinations and letting sixteen of them answer 403 is worse than showing three. The
   * backend remains the thing that enforces; this only decides what is worth offering.
   */
  roles?: readonly string[];
}

export interface NavGroup {
  label?: string;
  items: NavItem[];
}

/**
 * Filters the nav to what a caller may actually reach. SuperAdmin sees everything, matching the
 * backend, where SensitivityService and the role checks both short-circuit for it.
 *
 * A group whose every item is filtered out is dropped, so the sidebar does not render an empty
 * "Access" heading with nothing under it.
 */
export function visibleGroups(groups: NavGroup[], userRoles: readonly string[] | undefined): NavGroup[] {
  const roles = userRoles ?? [];
  if (roles.includes('SuperAdmin')) return groups;

  return groups
    .map((g) => ({ ...g, items: g.items.filter((i) => !i.roles || i.roles.some((r) => roles.includes(r))) }))
    .filter((g) => g.items.length > 0);
}

export const NAV_GROUPS: NavGroup[] = [
  {
    items: [{ title: 'Overview', href: '/', icon: IconDashboard }],
  },
  {
    label: 'Content',
    items: [
      { title: 'Content types', href: '/schemas', icon: IconContentTypes , roles: ['SuperAdmin', 'Admin', 'Editor'] },
      { title: 'Entries', href: '/content', icon: IconContent , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Workflows', href: '/workflows', icon: IconWorkflows , roles: ['SuperAdmin', 'Admin'] },
    ],
  },
  {
    label: 'Access',
    items: [
      { title: 'Tenants', href: '/tenants', icon: IconServer , roles: ['SuperAdmin'] },
      { title: 'Users', href: '/users', icon: IconUsers , roles: ['SuperAdmin'] },
      { title: 'Roles', href: '/roles', icon: IconRoles , roles: ['SuperAdmin'] },
      { title: 'Groups', href: '/user-groups', icon: IconGroups , roles: ['SuperAdmin', 'Admin'] },
      { title: 'API keys', href: '/api-keys', icon: IconKey , roles: ['SuperAdmin', 'Admin'] },
    ],
  },
  {
    label: 'Modules',
    items: [
      { title: 'Accounting', href: '/accounting', icon: IconCoins , roles: ['SuperAdmin', 'Admin', 'Accountant'] },
      { title: 'Analytics', href: '/analytics', icon: IconAnalytics , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Email events', href: '/email-events', icon: IconEnvelope , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Feature flags', href: '/feature-flags', icon: IconFlag , roles: ['SuperAdmin', 'Admin'] },
      { title: 'PWA installs', href: '/pwa', icon: IconMobile , roles: ['SuperAdmin', 'Admin'] },
    ],
  },
  {
    label: 'System',
    items: [
      { title: 'Audit log', href: '/audit', icon: IconHistory , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Errors', href: '/errors', icon: IconBug , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Health', href: '/ops/health', icon: IconHealth },
      { title: 'Email', href: '/settings/email', icon: IconEnvelope , roles: ['SuperAdmin'] },
      { title: 'Security', href: '/settings/security', icon: IconShield , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Settings', href: '/settings', icon: IconSettings , roles: ['SuperAdmin', 'Admin'] },
    ],
  },
];

const SEGMENT_TITLES: Record<string, string> = {
  email: 'Email',
  schemas: 'Content types',
  content: 'Entries',
  workflows: 'Workflows',
  users: 'Users',
  roles: 'Roles',
  'user-groups': 'Groups',
  ops: 'System',
  health: 'Health',
  analytics: 'Analytics',
  accounting: 'Accounting',
  errors: 'Errors',
  audit: 'Audit log',
  'email-events': 'Email events',
  'feature-flags': 'Feature flags',
  pwa: 'PWA installs',
  settings: 'Settings',
  new: 'New',
};

export function breadcrumbsFor(pathname: string): { title: string; href: string }[] {
  const segments = pathname.split('/').filter(Boolean);
  return segments.map((segment, i) => ({
    title: SEGMENT_TITLES[segment] ?? decodeURIComponent(segment),
    href: '/' + segments.slice(0, i + 1).join('/'),
  }));
}

export function isNavItemActive(href: string, pathname: string): boolean {
  if (href === '/') return pathname === '/';
  return pathname === href || pathname.startsWith(href + '/');
}
