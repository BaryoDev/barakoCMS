// "What's new" changelog shown in the admin. Add a new entry at the TOP of RELEASES when you ship.
//
// There is deliberately no CURRENT_VERSION constant here any more. The unseen dot is driven off the
// version the running API reports from /api/meta, because the hand-maintained constant that used to
// drive it sat at 3.1.2 while the product was at 3.20.1 — nineteen minor versions of the dot never
// appearing for anyone. A number somebody has to remember to bump is not a mechanism.
//
// The dialog never opens on its own. The dot is the only proactive element.

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

export const RELEASES: Release[] = [
    {
        version: '3.21.0',
        date: 'August 2026',
        items: [
            {
                type: 'fix',
                title: 'Content marked Sensitive was stored as Public',
                description:
                    'Sensitivity chosen when creating an entry was dropped on the way to storage, so entries posted as Sensitive or Hidden were saved as Public and the redaction rules never applied to them.',
            },
            {
                type: 'fix',
                title: 'Unsigned Resend webhooks are refused',
                description:
                    'When no signing secret was configured, inbound email webhooks were accepted without verification. An unconfigured instance now rejects them instead of trusting them.',
            },
            {
                type: 'fix',
                title: 'The PWA report endpoint is rate limited',
                description: 'It accepted unlimited anonymous submissions.',
            },
            {
                type: 'improvement',
                title: 'The sidebar shows only what your role can open',
                description:
                    'Every account used to see all nineteen destinations, and most of them answered with a permission error on arrival. The navigation now matches what you can actually reach.',
            },
            {
                type: 'feature',
                title: 'About dialog, and a version you can read',
                description:
                    'The sidebar footer shows the version of the API this admin is connected to, and opens an About dialog with the API address, documentation, release notes and where to report an issue.',
            },
            {
                type: 'improvement',
                title: 'Releases ship the build that was tested',
                description:
                    'The pipeline now compiles once and publishes that exact output, and installs every package into a scratch project before pushing it, so a package that cannot be consumed never reaches NuGet.',
            },
            {
                type: 'feature',
                title: 'Modules declare a contract version',
                description:
                    'A module built against an incompatible core is now refused at startup with a clear message, rather than failing later in a way that is hard to trace.',
            },
            {
                type: 'improvement',
                title: 'Node 22 in the admin image',
                description: 'Node 20 reached end of life and no longer receives security updates.',
            },
        ],
    },
    {
        version: '3.1.2',
        date: 'July 2026',
        items: [
            {
                type: 'fix',
                title: 'Failed lists no longer look empty',
                description:
                    'When the API could not be reached, Content, Users, Roles, Groups, Content types and Workflows showed their "nothing here yet" message — which read as though the data was gone. They now say the request failed and offer a retry.',
            },
            {
                type: 'improvement',
                title: 'Errors page paginates',
                description:
                    'The list was capped at the first 25 errors. It now pages through the full set, and changing a filter or the search box returns you to page one.',
            },
            {
                type: 'improvement',
                title: 'Balances as at any date',
                description:
                    'Accounting balances take an "as at" date, so you can see where the books stood at the end of any past day instead of only today.',
            },
            {
                type: 'improvement',
                title: 'Readable secondary text',
                description:
                    'Muted text and warning badges were too light to meet the WCAG AA contrast minimum. Both are darker now.',
            },
            {
                type: 'fix',
                title: 'Error rows open with the keyboard',
                description:
                    'Error detail could only be opened by clicking a row, leaving it unreachable by keyboard. Rows are now focusable and respond to Enter or Space.',
            },
            {
                type: 'fix',
                title: 'What’s new keeps its header while you scroll',
                description:
                    'The title scrolled away with the release notes. It stays put now, and the dialog no longer opens by itself — the dot on the toolbar button marks unread notes instead.',
            },
            {
                type: 'improvement',
                title: 'Animations respect your system settings',
                description:
                    'If your OS is set to reduce motion, the admin now skips its transitions and animations.',
            },
        ],
    },
    {
        version: '3.1.0',
        date: 'July 2026',
        items: [
            {
                type: 'feature',
                title: 'Switch between tenants',
                description:
                    'On a multi-tenant deployment the admin scopes to your tenant automatically, and a switcher in the top bar lets you move between the tenants you belong to. All data reloads under the one you pick.',
            },
            {
                type: 'feature',
                title: 'Installed modules now show up in the admin',
                description:
                    'New sections surface the data from modules you have installed: Accounting (chart of accounts, balances, ledgers), Feature flags (view and toggle), Email events (Resend bounces and complaints), and Errors (the client-side error log with a resolve action).',
            },
            {
                type: 'feature',
                title: 'Analytics breaks visitors down by device, OS and browser',
                description:
                    'The Analytics page now shows devices, operating systems (including Android and iOS) and browsers, alongside pages, referrers and countries.',
            },
            {
                type: 'improvement',
                title: 'Add a website and confirm it is tracking',
                description:
                    'Adding a site in Analytics now gives step-by-step instructions and a Verify button that checks whether Umami is receiving data yet, so you know the snippet is live before you walk away.',
            },
        ],
    },
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
