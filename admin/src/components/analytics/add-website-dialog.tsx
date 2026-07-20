'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { IconPlus, IconCopy } from '@/components/icons';
import { useCreateWebsite, type CreatedWebsite } from '@/hooks/use-analytics';

/** Registers a new site in Umami and shows the tracking snippet to paste — no leaving the admin. */
export function AddWebsiteDialog({ onCreated }: { onCreated?: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [domain, setDomain] = useState('');
  const [created, setCreated] = useState<CreatedWebsite | null>(null);
  const create = useCreateWebsite();

  function reset() {
    setName('');
    setDomain('');
    setCreated(null);
    create.reset();
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    try {
      const site = await create.mutateAsync({ name: name.trim(), domain: domain.trim() });
      setCreated(site);
      onCreated?.(site.id);
    } catch {
      toast.error('Could not add the site. Check the Umami connection and try again.');
    }
  }

  function copySnippet() {
    if (!created) return;
    navigator.clipboard.writeText(created.snippet).then(
      () => toast.success('Snippet copied'),
      () => toast.error('Copy failed — select and copy manually'),
    );
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        setOpen(o);
        if (!o) reset();
      }}
    >
      <DialogTrigger asChild>
        <Button variant="outline" size="sm">
          <IconPlus className="size-4" />
          Add website
        </Button>
      </DialogTrigger>
      <DialogContent>
        {!created ? (
          <form onSubmit={submit}>
            <DialogHeader>
              <DialogTitle>Track a new website</DialogTitle>
              <DialogDescription>
                Registers the site in Umami and gives you a snippet to paste into its pages.
              </DialogDescription>
            </DialogHeader>
            <div className="grid gap-4 py-4">
              <div className="grid gap-2">
                <Label htmlFor="site-name">Name</Label>
                <Input
                  id="site-name"
                  placeholder="BaryoClub"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  required
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="site-domain">Domain</Label>
                <Input
                  id="site-domain"
                  placeholder="club.baryo.dev"
                  value={domain}
                  onChange={(e) => setDomain(e.target.value)}
                  required
                />
                <p className="text-muted-foreground text-xs">
                  A bare domain, no https:// and no path.
                </p>
              </div>
            </div>
            <DialogFooter>
              <Button type="submit" disabled={create.isPending}>
                {create.isPending ? 'Adding…' : 'Add website'}
              </Button>
            </DialogFooter>
          </form>
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>{created.name} is ready</DialogTitle>
              <DialogDescription>
                Paste this into the <code className="text-xs">&lt;head&gt;</code> of {created.domain}. Data
                appears within a minute of the first visit.
              </DialogDescription>
            </DialogHeader>
            <div className="py-4">
              <pre className="bg-muted overflow-x-auto rounded-md p-3 text-xs leading-relaxed">
                {created.snippet}
              </pre>
            </div>
            <DialogFooter>
              <Button variant="outline" onClick={copySnippet}>
                <IconCopy className="size-4" />
                Copy snippet
              </Button>
              <Button
                onClick={() => {
                  setOpen(false);
                  reset();
                }}
              >
                Done
              </Button>
            </DialogFooter>
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}
