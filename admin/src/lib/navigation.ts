import type { ComponentType, SVGProps } from 'react';
import {
  IconAnalytics,
  IconArchive,
  IconBug,
  IconCoins,
  IconContent,
  IconContentTypes,
  IconDashboard,
  IconEnvelope,
  IconFlag,
  IconGroups,
  IconHealth,
  IconHistory,
  IconKey,
  IconMobile,
  IconRoles,
  IconServer,
  IconSettings,
  IconShield,
  IconUsers,
  IconWorkflows,
} from '@/components/icons';

/**
 * Names a live number the rail may show beside an item. It is an identifier, not a value: the
 * component resolves it through `useNavMetrics`, and an unresolved one renders nothing rather than
 * a placeholder. A count nobody can source is left off the item entirely.
 */
export type NavMetric =
  | 'entries'
  | 'contentTypes'
  | 'workflows'
  | 'unresolvedErrors'
  | 'recentBounces';

export interface NavItem {
  title: string;
  href: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  /** A count rendered right-aligned in mono, or a tinted pill when `tone` says so. */
  metric?: NavMetric;
  /** Pill tint for a metric that reports a problem rather than a size. */
  tone?: 'warning' | 'danger';
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

/**
 * The first group carries no label on purpose: it is the primary set, the four destinations someone
 * works in all day, and a heading over them would only name the app. Every group after it is
 * labelled, and the rail renders those at a smaller size.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    items: [
      { title: 'Overview', href: '/', icon: IconDashboard },
      { title: 'Entries', href: '/content', icon: IconContent, metric: 'entries', roles: ['SuperAdmin', 'Admin'] },
      // Editor was removed when #373 took that grant off GET /api/content-types. Leaving it here
      // rendered a link the API answered 403 to, and nothing creates an Editor role anyway.
      { title: 'Content types', href: '/schemas', icon: IconContentTypes, metric: 'contentTypes', roles: ['SuperAdmin', 'Admin'] },
      { title: 'Workflows', href: '/workflows', icon: IconWorkflows, metric: 'workflows', roles: ['SuperAdmin', 'Admin'] },
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
      { title: 'Email events', href: '/email-events', icon: IconEnvelope, metric: 'recentBounces', tone: 'warning', roles: ['SuperAdmin', 'Admin'] },
      { title: 'Feature flags', href: '/feature-flags', icon: IconFlag , roles: ['SuperAdmin', 'Admin'] },
      { title: 'PWA installs', href: '/pwa', icon: IconMobile , roles: ['SuperAdmin', 'Admin'] },
    ],
  },
  {
    label: 'System',
    items: [
      { title: 'Audit log', href: '/audit', icon: IconHistory , roles: ['SuperAdmin', 'Admin'] },
      { title: 'Errors', href: '/errors', icon: IconBug, metric: 'unresolvedErrors', tone: 'danger', roles: ['SuperAdmin', 'Admin'] },
      { title: 'Health', href: '/ops/health', icon: IconHealth },
      { title: 'Email', href: '/settings/email', icon: IconEnvelope , roles: ['SuperAdmin'] },
      { title: 'Security', href: '/settings/security', icon: IconShield , roles: ['SuperAdmin', 'Admin'] },
      // Every seeded role, because GET /api/devices is scoped to the caller and lists their own
      // devices: an ordinary User has as much right to it as an Admin. Named rather than left
      // ungated, since an item with no roles is offered to a signed-out caller too, and Overview
      // and Health are the only two that should be.
      { title: 'Devices', href: '/settings/devices', icon: IconMobile , roles: ['SuperAdmin', 'Admin', 'User'] },
      { title: 'Export and import', href: '/settings/portability', icon: IconArchive , roles: ['SuperAdmin', 'Admin'] },
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
  devices: 'Devices',
  portability: 'Export and import',
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
