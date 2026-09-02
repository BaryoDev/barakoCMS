'use client';

import { useEffect } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { AppSidebar } from '@/components/app-sidebar';
import { AppHeader } from '@/components/app-header';
import { SidebarInset, SidebarProvider } from '@/components/ui/sidebar';
import { BrandMark } from '@/components/brand';

export default function AdminLayout({ children }: { children: React.ReactNode }) {
  const { isAuthenticated, isLoading, requireAuth } = useAuth();

  useEffect(() => {
    requireAuth();
  }, [requireAuth]);

  if (isLoading || !isAuthenticated) {
    return (
      <div className="flex min-h-svh items-center justify-center">
        <BrandMark className="animate-pulse" />
      </div>
    );
  }

  return (
    // 248px of rail, 16px of it padding. The rail sits on the page background and the content panel
    // is inset away from it on three sides, which is what makes the panel read as a card rather
    // than the other half of a split screen.
    <SidebarProvider style={{ '--sidebar-width': '248px' } as React.CSSProperties}>
      <AppSidebar />
      <SidebarInset className="md:my-4 md:mr-4 md:ml-0 md:h-[calc(100svh-2rem)] md:overflow-hidden md:rounded-xl md:border md:bg-card md:shadow-[var(--shadow-card)]">
        <AppHeader />
        <main className="mx-auto w-full max-w-6xl flex-1 p-4 md:overflow-auto md:p-6">{children}</main>
      </SidebarInset>
    </SidebarProvider>
  );
}
