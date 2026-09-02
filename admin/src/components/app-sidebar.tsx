'use client';

import { useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/hooks/use-auth';
import { BrandMark, BrandWordmark } from '@/components/brand';
import { NAV_GROUPS, isNavItemActive, visibleGroups } from '@/lib/navigation';
import { IconMore, IconSignOut, IconUser } from '@/components/icons';
import { AboutDialog } from '@/components/about-dialog';
import { useApiMeta } from '@/hooks/use-meta';
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

export function AppSidebar() {
  const pathname = usePathname();
  const { user, logout } = useAuth();

  // Filtered rather than rendered whole. Every item used to be shown to every role, so a User saw
  // all nineteen destinations and sixteen of them answered 403 on arrival. The backend was never
  // the problem; the sidebar was advertising doors it knew were locked.
  const groups = visibleGroups(NAV_GROUPS, user?.roles);
  const { data: meta } = useApiMeta();
  const [aboutOpen, setAboutOpen] = useState(false);

  return (
    <Sidebar collapsible="icon">
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild>
              <Link href="/">
                <BrandMark />
                <BrandWordmark className="group-data-[collapsible=icon]:hidden" />
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        {groups.map((group, i) => (
          <SidebarGroup key={group.label ?? i}>
            {group.label && <SidebarGroupLabel>{group.label}</SidebarGroupLabel>}
            <SidebarGroupContent>
              <SidebarMenu>
                {group.items.map((item) => (
                  <SidebarMenuItem key={item.href}>
                    <SidebarMenuButton
                      asChild
                      isActive={isNavItemActive(item.href, pathname)}
                      tooltip={item.title}
                    >
                      <Link href={item.href}>
                        <item.icon />
                        <span>{item.title}</span>
                      </Link>
                    </SidebarMenuButton>
                  </SidebarMenuItem>
                ))}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        ))}
      </SidebarContent>

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <SidebarMenuButton size="lg" tooltip="Account">
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-secondary text-secondary-foreground">
                    <IconUser className="size-4" />
                  </div>
                  <div className="grid flex-1 text-left leading-tight group-data-[collapsible=icon]:hidden">
                    <span className="truncate text-sm font-medium">{user?.username ?? 'Account'}</span>
                    <span className="truncate text-xs text-muted-foreground">
                      {user?.roles.join(', ') || 'Signed in'}
                    </span>
                  </div>
                  <IconMore className="ml-auto size-4 group-data-[collapsible=icon]:hidden" />
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
                <DropdownMenuSeparator />
                <DropdownMenuItem onClick={logout} variant="destructive">
                  <IconSignOut />
                  Sign out
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </SidebarMenuItem>
        </SidebarMenu>

        {/* Hidden when the sidebar collapses to icons, and hidden entirely until the API has
            answered — a version line reading "unknown" in the chrome is worse than no line. */}
        {meta?.version && (
          <button
            type="button"
            onClick={() => setAboutOpen(true)}
            className="px-2 pb-1 text-left text-xs text-muted-foreground hover:text-foreground group-data-[collapsible=icon]:hidden"
          >
            BarakoCMS {meta.version}
          </button>
        )}
      </SidebarFooter>

      <AboutDialog open={aboutOpen} onOpenChange={setAboutOpen} />
    </Sidebar>
  );
}
