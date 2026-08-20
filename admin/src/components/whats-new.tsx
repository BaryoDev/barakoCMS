'use client';

import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';
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
import { CHANGE_META, RELEASES } from '@/lib/whats-new';
import { useApiMeta } from '@/hooks/use-meta';

const SEEN_KEY = 'barako_whats_new_seen';

// Returned instead of the stored value when localStorage is unavailable. It can never equal a real
// version, so every comparison against it has to be made explicitly rather than accidentally
// passing — see `unseen` below, which treats it as "seen" on purpose.
const STORAGE_BLOCKED = Symbol('storage-blocked');

// The last version this browser acknowledged, read through useSyncExternalStore so it
// survives SSR without a hydration mismatch. The server snapshot is null, which reads as
// "nothing acknowledged yet"; the dot still stays off during SSR because the API version
// is not known until the client has fetched it.
let listeners: Array<() => void> = [];

function subscribeSeen(onChange: () => void) {
    listeners = [...listeners, onChange];
    return () => {
        listeners = listeners.filter((l) => l !== onChange);
    };
}

function seenSnapshot(): string | null | typeof STORAGE_BLOCKED {
    try {
        return localStorage.getItem(SEEN_KEY);
    } catch {
        return STORAGE_BLOCKED; // can't remember a dismissal, so don't nag
    }
}

const seenServerSnapshot = (): string | null | typeof STORAGE_BLOCKED => null;

// Lets the About dialog open these release notes. Same module-listener shape as `listeners` above,
// rather than a context, because there is exactly one WhatsNew mounted and it lives in the header
// while About lives in the sidebar footer.
let openRequests: Array<() => void> = [];

export function requestOpenWhatsNew() {
    openRequests.forEach((f) => f());
}

export function WhatsNew() {
    const [open, setOpen] = useState(false);
    const seen = useSyncExternalStore(subscribeSeen, seenSnapshot, seenServerSnapshot);
    const { data: meta } = useApiMeta();
    const apiVersion = meta?.version ?? null;

    // Flag the button when the running API reports a version this browser has not acknowledged.
    // While the version is unknown (still loading, or /api/meta failed) there is no dot at all,
    // rather than a dot that appears and then disappears.
    //
    // Deliberately does not auto-open — a first-time admin should land on their dashboard,
    // not on release notes. The dot is the invitation.
    const unseen = apiVersion !== null && seen !== STORAGE_BLOCKED && seen !== apiVersion;

    // useCallback so the effect below can depend on it honestly: markSeen closes over apiVersion,
    // and the registered handler has to be replaced when that arrives. Otherwise opening from
    // About before /api/meta resolves would never clear the dot.
    const markSeen = useCallback(() => {
        if (apiVersion === null) return; // nothing to record yet
        try {
            localStorage.setItem(SEEN_KEY, apiVersion);
        } catch {
            /* ignore */
        }
        listeners.forEach((l) => l());
    }, [apiVersion]);

    useEffect(() => {
        const openFromAbout = () => {
            setOpen(true);
            markSeen();
        };
        openRequests = [...openRequests, openFromAbout];
        return () => {
            openRequests = openRequests.filter((f) => f !== openFromAbout);
        };
    }, [markSeen]);

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
