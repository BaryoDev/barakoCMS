'use client';

import { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { NAV_GROUPS } from '@/lib/navigation';
import { IconPlus, IconSearch } from '@/components/icons';
import { Button } from '@/components/ui/button';
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from '@/components/ui/command';

export function CommandMenu() {
  const [open, setOpen] = useState(false);
  const router = useRouter();

  useEffect(() => {
    const down = (e: KeyboardEvent) => {
      if (e.key === 'k' && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        setOpen((open) => !open);
      }
    };
    document.addEventListener('keydown', down);
    return () => document.removeEventListener('keydown', down);
  }, []);

  const run = useCallback((command: () => void) => {
    setOpen(false);
    command();
  }, []);

  return (
    <>
      {/* Lives in the sidebar rail, so it is sized to the rail: full width, 40px, a white field on
          the page background rather than a compact header control. */}
      <Button
        variant="outline"
        className="h-10 w-full justify-start gap-2 rounded-[11px] bg-card px-3 font-normal text-muted-foreground shadow-[var(--shadow-card)] hover:text-foreground"
        onClick={() => setOpen(true)}
      >
        <IconSearch className="size-3.5" />
        <span className="flex-1 text-left text-[13px]">Search or jump to</span>
        <kbd className="bg-secondary text-(--faint) pointer-events-none rounded-[6px] px-1.5 py-0.5 font-mono text-[10px]">
          ⌘K
        </kbd>
      </Button>
      <CommandDialog open={open} onOpenChange={setOpen}>
        <CommandInput placeholder="Go to a page or run an action…" />
        <CommandList>
          <CommandEmpty>Nothing matches that search.</CommandEmpty>
          <CommandGroup heading="Go to">
            {NAV_GROUPS.flatMap((g) => g.items).map((item) => (
              <CommandItem key={item.href} onSelect={() => run(() => router.push(item.href))}>
                <item.icon />
                {item.title}
              </CommandItem>
            ))}
          </CommandGroup>
          <CommandSeparator />
          <CommandGroup heading="Create">
            <CommandItem onSelect={() => run(() => router.push('/schemas/new'))}>
              <IconPlus />
              New content type
            </CommandItem>
            <CommandItem onSelect={() => run(() => router.push('/content/new'))}>
              <IconPlus />
              New entry
            </CommandItem>
            <CommandItem onSelect={() => run(() => router.push('/workflows/new'))}>
              <IconPlus />
              New workflow
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </CommandDialog>
    </>
  );
}
