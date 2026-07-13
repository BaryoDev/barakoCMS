import type { ComponentType, SVGProps } from 'react';
import {
  IconDashboard,
  IconContentTypes,
  IconContent,
  IconWorkflows,
  IconUsers,
  IconRoles,
  IconGroups,
  IconHealth,
  IconSettings,
} from '@/components/icons';

export interface NavItem {
  title: string;
  href: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
}

export interface NavGroup {
  label?: string;
  items: NavItem[];
}

export const NAV_GROUPS: NavGroup[] = [
  {
    items: [{ title: 'Overview', href: '/', icon: IconDashboard }],
  },
  {
    label: 'Content',
    items: [
      { title: 'Content types', href: '/schemas', icon: IconContentTypes },
      { title: 'Entries', href: '/content', icon: IconContent },
      { title: 'Workflows', href: '/workflows', icon: IconWorkflows },
    ],
  },
  {
    label: 'Access',
    items: [
      { title: 'Users', href: '/users', icon: IconUsers },
      { title: 'Roles', href: '/roles', icon: IconRoles },
      { title: 'Groups', href: '/user-groups', icon: IconGroups },
    ],
  },
  {
    label: 'System',
    items: [
      { title: 'Health', href: '/ops/health', icon: IconHealth },
      { title: 'Settings', href: '/settings', icon: IconSettings },
    ],
  },
];

const SEGMENT_TITLES: Record<string, string> = {
  schemas: 'Content types',
  content: 'Entries',
  workflows: 'Workflows',
  users: 'Users',
  roles: 'Roles',
  'user-groups': 'Groups',
  ops: 'System',
  health: 'Health',
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
