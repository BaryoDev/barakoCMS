'use client';

import {
    Dialog,
    DialogContent,
    DialogDescription,
    DialogHeader,
    DialogTitle,
} from '@/components/ui/dialog';
import { Separator } from '@/components/ui/separator';
import { IconBolt, IconBug, IconCoffee, IconExternalLink, IconInfo, IconUsers } from '@/components/icons';
import { getApiUrl } from '@/lib/api';
import { adminVersion, useApiMeta } from '@/hooks/use-meta';
import { requestOpenWhatsNew } from '@/components/whats-new';

const REPO = 'https://github.com/BaryoDev/barakoCMS';

// The README is the user documentation, in practice. barakocms.baryo.dev has no /docs route and
// docs/ in the repository is design notes, so pointing anywhere else would be a broken promise.
const DOCS_URL = `${REPO}#readme`;
const ISSUES_URL = `${REPO}/issues/new`;
const DISCORD_URL = 'https://discord.gg/7GYKzDx7Z2';
const SPONSOR_URL = 'https://github.com/sponsors/BaryoDev';

function Row({ label, value }: { label: string; value: string }) {
    return (
        <div className="flex items-baseline justify-between gap-4 text-sm">
            <span className="text-muted-foreground">{label}</span>
            <span className="font-mono text-xs">{value}</span>
        </div>
    );
}

function LinkRow({
    icon,
    label,
    href,
    onClick,
}: {
    icon: React.ReactNode;
    label: string;
    href?: string;
    onClick?: () => void;
}) {
    const className =
        'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent hover:text-accent-foreground';

    if (onClick) {
        return (
            <button type="button" onClick={onClick} className={className}>
                {icon}
                {label}
            </button>
        );
    }

    return (
        <a href={href} target="_blank" rel="noreferrer noopener" className={className}>
            {icon}
            <span className="flex-1 text-left">{label}</span>
            <IconExternalLink className="size-3 text-muted-foreground" aria-hidden />
        </a>
    );
}

export function AboutDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
    const { data: meta } = useApiMeta();
    const admin = adminVersion();

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent className="max-w-sm">
                <DialogHeader>
                    <DialogTitle>About BarakoCMS</DialogTitle>
                    <DialogDescription>What this admin is connected to, and where to get help.</DialogDescription>
                </DialogHeader>

                <div className="space-y-1.5">
                    {/* "unknown" rather than a blank or a spinner: not knowing is itself the answer to
                        "what am I running", and a reader can act on it. */}
                    <Row label="API version" value={meta?.version ?? 'unknown'} />
                    <Row label="Admin version" value={admin ?? 'unknown'} />
                    <Row label="API address" value={getApiUrl()} />
                </div>

                <Separator />

                <div className="space-y-0.5">
                    <LinkRow icon={<IconInfo className="size-4" />} label="Documentation" href={DOCS_URL} />

                    {/* Only when the instance actually serves it. Swagger is off by default outside
                        Development, so most self-hosters have nothing at this address. */}
                    {meta?.swaggerEnabled && (
                        <LinkRow
                            icon={<IconExternalLink className="size-4" />}
                            label="API reference"
                            href={`${getApiUrl()}/swagger`}
                        />
                    )}

                    <LinkRow
                        icon={<IconBolt className="size-4" />}
                        label="What's new"
                        onClick={() => {
                            onOpenChange(false);
                            requestOpenWhatsNew();
                        }}
                    />
                    <LinkRow icon={<IconBug className="size-4" />} label="Report an issue" href={ISSUES_URL} />
                    <LinkRow icon={<IconUsers className="size-4" />} label="Discord" href={DISCORD_URL} />

                    {/* One link among several, in a dialog the user chose to open, with no persuasion
                        copy. Someone who never opens About never sees it. */}
                    <LinkRow icon={<IconCoffee className="size-4" />} label="Sponsor" href={SPONSOR_URL} />
                </div>
            </DialogContent>
        </Dialog>
    );
}
