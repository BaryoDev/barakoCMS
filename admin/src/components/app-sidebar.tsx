'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/hooks/use-auth';
import { BrandMark, BrandWordmark } from '@/components/brand';
import { NAV_GROUPS, isNavItemActive, visibleGroups, type NavGroup, type NavItem } from '@/lib/navigation';
import { useNavMetrics, type NavMetrics } from '@/hooks/use-nav-metrics';
import { CommandMenu } from '@/components/command-menu';
import { IconMore, IconSignOut } from '@/components/icons';
import { AboutDialog } from '@/components/about-dialog';
import { useApiMeta } from '@/hooks/use-meta';
import { cn } from '@/lib/utils';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
} from '@/components/ui/sidebar';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';

/** Two letters from a username, so `demo_admin` reads as DA and `rosa` as RO. */
function initialsOf(username: string | undefined): string {
  if (!username) return '?';
  const parts = username.split(/[^A-Za-z0-9]+/).filter(Boolean);
  const letters = parts.length > 1 ? parts[0][0] + parts[1][0] : username.slice(0, 2);
  return letters.toUpperCase();
}

/**
 * The number beside a nav item. Three shapes, all of them real:
 *  - a plain mono count on the primary items, right-aligned, saying how big the thing is;
 *  - a tinted pill when the item declares a tone, saying something needs attention;
 *  - nothing at all when the metric has no source, which is the default.
 *
 * The screen-reader text is not decoration. "Errors 2" on its own does not say two of what.
 */
function NavMetricLabel({ item, metrics }: { item: NavItem; metrics: NavMetrics }) {
  const metric = item.metric ? metrics[item.metric] : undefined;
  if (!metric) return null;

  // A tinted pill exists to report a problem. Nothing wrong, nothing shown.
  if (item.tone && metric.value === 0) return null;

  const text = metric.atLeast ? `${metric.value}+` : `${metric.value}`;

  if (!item.tone) {
    return (
      <span className="font-mono text-[11px] tabular-nums text-(--faint)">{text}</span>
    );
  }

  return (
    <span
      className={cn(
        'flex h-5 min-w-5 items-center justify-center rounded-full px-1.5 font-mono text-[10.5px] font-bold tabular-nums',
        item.tone === 'danger'
          ? 'bg-(--danger-soft) text-destructive'
          : 'bg-(--warning-soft) text-warning'
      )}
    >
      {text}
      <span className="sr-only">
        {item.tone === 'danger' ? ' unresolved' : ' bounced in the last 24 hours'}
      </span>
    </span>
  );
}

function NavList({
  group,
  primary,
  pathname,
  metrics,
}: {
  group: NavGroup;
  primary: boolean;
  pathname: string;
  metrics: NavMetrics;
}) {
  return (
    <SidebarMenu className="gap-0.5">
      {group.items.map((item) => {
        const active = isNavItemActive(item.href, pathname);
        return (
          <SidebarMenuItem key={item.href}>
            <SidebarMenuButton
              asChild
              isActive={active}
              tooltip={item.title}
              className={cn(
                'gap-2.5 rounded-[10px] px-2.5 font-medium transition-colors',
                primary ? 'h-[38px] text-[13.5px]' : 'h-[34px] text-[13px]',
                'text-secondary-foreground hover:bg-secondary hover:text-foreground',
                // The active item is a white card lifted off the page background, not a tint.
                'data-[active=true]:bg-card data-[active=true]:text-foreground data-[active=true]:font-semibold',
                'data-[active=true]:shadow-[var(--shadow-raised)]',
                active ? '[&>svg]:text-primary' : '[&>svg]:text-(--faint)'
              )}
            >
              <Link href={item.href}>
                <item.icon />
                <span className="flex-1 truncate">{item.title}</span>
                <NavMetricLabel item={item} metrics={metrics} />
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        );
      })}
    </SidebarMenu>
  );
}

export function AppSidebar() {
  const pathname = usePathname();
  const { user, logout } = useAuth();

  // Filtered rather than rendered whole. Every item used to be shown to every role, so a User saw
  // all nineteen destinations and sixteen of them answered 403 on arrival. The backend was never
  // the problem; the sidebar was advertising doors it knew were locked.
  const groups = visibleGroups(NAV_GROUPS, user?.roles);
  const { data: meta } = useApiMeta();
  const [aboutOpen, setAboutOpen] = useState(false);

  // Only the destinations that survived filtering get a count fetched for them.
  const visibleHrefs = useMemo(
    () => new Set(groups.flatMap((g) => g.items.map((i) => i.href))),
    [groups]
  );
  const metrics = useNavMetrics(visibleHrefs);

  return (
    <Sidebar collapsible="offcanvas" className="border-none">
      <SidebarHeader className="gap-3 p-4 pb-2">
        <div className="flex items-center gap-2.5">
          <Link href="/" className="flex min-w-0 items-center gap-2.5 rounded-[10px] outline-hidden focus-visible:ring-2 focus-visible:ring-ring/50">
            <BrandMark className="size-[30px] rounded-[9px]" />
            <BrandWordmark />
          </Link>
          {/* Hidden until the API has answered: a version line reading "unknown" is worse than none. */}
          {meta?.version && (
            <button
              type="button"
              onClick={() => setAboutOpen(true)}
              className="ml-auto rounded-sm font-mono text-[11px] tabular-nums text-(--faint) outline-hidden hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/50"
            >
              {meta.version}
              <span className="sr-only"> (about this instance)</span>
            </button>
          )}
        </div>

        <CommandMenu />
      </SidebarHeader>

      <SidebarContent className="px-4 pb-2">
        {groups.map((group, i) => {
          const primary = !group.label;
          return (
            <SidebarGroup key={group.label ?? i} className="p-0 pt-3 first:pt-1">
              {group.label && (
                <SidebarGroupLabel className="h-6 px-2.5 text-[10.5px] font-extrabold tracking-[0.12em] text-(--faint) uppercase">
                  {group.label}
                </SidebarGroupLabel>
              )}
              <SidebarGroupContent>
                <NavList group={group} primary={primary} pathname={pathname} metrics={metrics} />
              </SidebarGroupContent>
            </SidebarGroup>
          );
        })}
      </SidebarContent>

      <SidebarFooter className="p-4 pt-2">
        <SidebarMenu>
          <SidebarMenuItem>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <SidebarMenuButton
                  size="lg"
                  tooltip="Account"
                  className="h-auto rounded-[11px] border border-border bg-card p-2.5 shadow-[var(--shadow-card)] hover:bg-card data-[state=open]:bg-card"
                >
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-full bg-foreground font-mono text-[11px] font-bold text-background">
                    {initialsOf(user?.username)}
                  </div>
                  <div className="grid flex-1 text-left leading-tight">
                    <span className="truncate text-[13px] font-semibold">{user?.username ?? 'Account'}</span>
                    <span className="truncate text-[11.5px] text-muted-foreground">
                      {user?.roles.join(', ') || 'Signed in'}
                    </span>
                  </div>
                  <IconMore className="ml-auto size-4 text-(--faint)" />
                </SidebarMenuButton>
              </DropdownMenuTrigger>
              <DropdownMenuContent side="top" align="start" className="w-56">
                <DropdownMenuLabel className="font-normal">
                  <div className="grid gap-0.5">
                    <span className="text-sm font-medium">{user?.username}</span>
                    <span className="text-xs text-muted-foreground">{user?.roles.join(', ')}</span>
                  </div>
                </DropdownMenuLabel>
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={logout} variant="destructive">
                  <IconSignOut />
                  Sign out
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>

      <AboutDialog open={aboutOpen} onOpenChange={setAboutOpen} />
    </Sidebar>
  );
}
