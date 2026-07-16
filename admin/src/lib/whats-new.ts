// "What's new" changelog shown in the admin. Bundled with the build so it always matches the
// deployed version. Add a new entry at the TOP of RELEASES and bump CURRENT_VERSION when you ship;
// editors see the dialog auto-open once per new version.

export type ChangeType = 'feature' | 'fix' | 'improvement';

export interface ChangeItem {
    type: ChangeType;
    title: string;
    description?: string;
}

export interface Release {
    version: string;
    date: string;
    items: ChangeItem[];
}

// Bumping this re-triggers the one-time auto-open for everyone.
export const CURRENT_VERSION = '3.0.0';

export const RELEASES: Release[] = [
    {
        version: '3.0.0',
        date: 'July 2026',
        items: [
            {
                type: 'feature',
                title: 'Field-level sensitivity',
                description:
                    'Mark fields Sensitive or Hidden per content type. They are masked per role when read, and roles that cannot see a field cannot write it either.',
            },
            {
                type: 'feature',
                title: 'Sensitivity controls in the schema editor',
                description:
                    'Set a field’s sensitivity, the roles allowed to see it, and how it is masked (remove, redact to ***, or show only the last 4).',
            },
            {
                type: 'fix',
                title: 'Version history ordering',
                description:
                    'History now shows the newest version as Current with no Restore; earlier versions are the ones you restore.',
            },
            {
                type: 'fix',
                title: 'System health page crash',
                description: 'The health page no longer errors on the hardened /health response.',
            },
            {
                type: 'improvement',
                title: 'Lists and history respect sensitivity',
                description:
                    'Sensitive data is masked in entry lists and version history too, not only when opening a single entry.',
            },
        ],
    },
];

export const CHANGE_META: Record<ChangeType, { label: string; variant: 'default' | 'secondary' | 'outline' }> = {
    feature: { label: 'New', variant: 'default' },
    fix: { label: 'Fixed', variant: 'secondary' },
    improvement: { label: 'Improved', variant: 'outline' },
};
