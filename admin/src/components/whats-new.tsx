'use client';

import { useState, useSyncExternalStore } from 'react';
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

// The last version this browser acknowledged, read through useSyncExternalStore so it
// survives SSR without a hydration mismatch. The server snapshot claims "already seen"
// so the dot only appears once the real localStorage value is known on the client.
let listeners: Array<() => void> = [];

function subscribeSeen(onChange: () => void) {
    listeners = [...listeners, onChange];
    return () => {
        listeners = listeners.filter((l) => l !== onChange);
    };
}

function seenSnapshot() {
    try {
        return localStorage.getItem(SEEN_KEY);
    } catch {
        return CURRENT_VERSION; // storage blocked — don't nag
    }
}

const seenServerSnapshot = () => CURRENT_VERSION;

export function WhatsNew() {
    const [open, setOpen] = useState(false);
    const seen = useSyncExternalStore(subscribeSeen, seenSnapshot, seenServerSnapshot);

    // Flag the button when the deployed version is newer than what this browser last saw.
    // Deliberately does not auto-open — a first-time admin should land on their dashboard,
    // not on release notes. The dot is the invitation.
    const unseen = seen !== CURRENT_VERSION;

    const markSeen = () => {
        try {
            localStorage.setItem(SEEN_KEY, CURRENT_VERSION);
        } catch {
            /* ignore */
        }
        listeners.forEach((l) => l());
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
                {/* Flex column, not the default grid: the header stays put and only the
                    release list scrolls. `min-h-0` lets the scroll area actually shrink. */}
                <DialogContent className="flex max-h-[80vh] flex-col overflow-hidden">
                    <DialogHeader className="shrink-0">
                        <DialogTitle>What&apos;s new</DialogTitle>
                        <DialogDescription>
                            Latest features, fixes, and improvements in BarakoCMS.
                        </DialogDescription>
                    </DialogHeader>
                    {/* -mx-6 px-6 puts the scrollbar on the dialog edge rather than inset. */}
                    <div className="-mx-6 min-h-0 flex-1 space-y-6 overflow-y-auto px-6 py-2">
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
