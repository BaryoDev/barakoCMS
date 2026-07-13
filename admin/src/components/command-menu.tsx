'use client';

import { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { useTheme } from 'next-themes';
import { NAV_GROUPS } from '@/lib/navigation';
import { IconMoon, IconPlus, IconSearch, IconSun } from '@/components/icons';
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
  const { resolvedTheme, setTheme } = useTheme();

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
      <Button
        variant="outline"
        size="sm"
        className="text-muted-foreground w-full max-w-48 justify-start gap-2 font-normal sm:w-48"
        onClick={() => setOpen(true)}
      >
        <IconSearch className="size-3.5" />
        <span className="flex-1 text-left">Search…</span>
        <kbd className="bg-muted text-muted-foreground pointer-events-none rounded border px-1.5 font-mono text-[10px]">
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
          <CommandSeparator />
          <CommandGroup heading="Theme">
            <CommandItem onSelect={() => run(() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark'))}>
              {resolvedTheme === 'dark' ? <IconSun /> : <IconMoon />}
              Switch to {resolvedTheme === 'dark' ? 'light' : 'dark'} theme
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </CommandDialog>
    </>
  );
}
