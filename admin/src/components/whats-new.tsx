'use client';

import { useEffect, useState } from 'react';
import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { IconBolt } from '@/components/icons';
import { CHANGE_META, CURRENT_VERSION, RELEASES } from '@/lib/whats-new';

const SEEN_KEY = 'barako_whats_new_seen';

export function WhatsNew() {
    const [open, setOpen] = useState(false);
    const [unseen, setUnseen] = useState(false);

    // Auto-open once when the deployed version is newer than what this browser last saw.
    useEffect(() => {
        let seen: string | null = null;
        try {
            seen = localStorage.getItem(SEEN_KEY);
        } catch {
            /* ignore */
        }
        if (seen !== CURRENT_VERSION) {
            setUnseen(true);
            setOpen(true);
        }
    }, []);

    const markSeen = () => {
        try {
            localStorage.setItem(SEEN_KEY, CURRENT_VERSION);
        } catch {
            /* ignore */
        }
        setUnseen(false);
    };

    return (
        <>
            <Button
                variant="ghost"
                size="icon"
                className="relative"
                aria-label="What's new"
                onClick={() => {
                    setOpen(true);
                    markSeen();
                }}
            >
                <IconBolt className="size-4" />
                {unseen && (
                    <span className="bg-primary absolute right-1.5 top-1.5 size-2 rounded-full" aria-hidden />
                )}
            </Button>

            <Dialog
                open={open}
                onOpenChange={(v) => {
                    setOpen(v);
                    if (!v) markSeen();
                }}
            >
                <DialogContent className="max-h-[80vh] overflow-y-auto">
                    <DialogHeader>
                        <DialogTitle>What&apos;s new</DialogTitle>
                        <DialogDescription>
                            Latest features, fixes, and improvements in BarakoCMS.
                        </DialogDescription>
                    </DialogHeader>
                    <div className="space-y-6 py-2">
                        {RELEASES.map((release) => (
                            <div key={release.version} className="space-y-3">
                                <div className="flex items-baseline gap-2">
                                    <h3 className="text-sm font-semibold">v{release.version}</h3>
                                    <span className="text-muted-foreground text-xs">{release.date}</span>
                                </div>
                                <ul className="space-y-3">
                                    {release.items.map((item, i) => (
                                        <li key={i} className="flex gap-3">
                                            <Badge
                                                variant={CHANGE_META[item.type].variant}
                                                className="mt-0.5 h-fit shrink-0 text-xs"
                                            >
                                                {CHANGE_META[item.type].label}
                                            </Badge>
                                            <div className="min-w-0">
                                                <p className="text-sm font-medium">{item.title}</p>
                                                {item.description && (
                                                    <p className="text-muted-foreground text-xs">{item.description}</p>
                                                )}
                                            </div>
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        ))}
                    </div>
                </DialogContent>
            </Dialog>
        </>
    );
}
