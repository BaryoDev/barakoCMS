import { describe, it, expect } from 'vitest';
import { NAV_GROUPS, visibleGroups } from './navigation';

const count = (roles: string[] | undefined) =>
    visibleGroups(NAV_GROUPS, roles).reduce((n, g) => n + g.items.length, 0);

const titles = (roles: string[] | undefined) =>
    visibleGroups(NAV_GROUPS, roles).flatMap((g) => g.items.map((i) => i.title));

describe('nav visibility', () => {
    // The control. Without it, a filter that hides everything passes every test below.
    it('shows SuperAdmin everything', () => {
        expect(count(['SuperAdmin'])).toBe(
            NAV_GROUPS.reduce((n, g) => n + g.items.length, 0),
        );
    });

    // The regression. A plain user used to see all nineteen destinations.
    it('does not show a plain user the admin destinations', () => {
        const seen = titles(['User']);
        expect(seen).not.toContain('Users');
        expect(seen).not.toContain('Roles');
        expect(seen).not.toContain('Tenants');
        expect(seen).not.toContain('API keys');
        expect(seen).not.toContain('Audit log');
    });

    it('shows fewer to Admin than to SuperAdmin, and fewer again to User', () => {
        expect(count(['Admin'])).toBeLessThan(count(['SuperAdmin']));
        expect(count(['User'])).toBeLessThan(count(['Admin']));
    });

    it('gives Editor the content types screen the API lets them reach', () => {
        expect(titles(['Editor'])).toContain('Content types');
    });

    it('gives Accountant the accounting screen and nothing extra', () => {
        const seen = titles(['Accountant']);
        expect(seen).toContain('Accounting');
        expect(seen).not.toContain('Users');
    });

    it('drops a group whose every item was filtered out', () => {
        // Otherwise the sidebar renders an "Access" heading with nothing under it.
        for (const g of visibleGroups(NAV_GROUPS, ['User'])) {
            expect(g.items.length).toBeGreaterThan(0);
        }
    });

    // The rail draws the first group without a heading and at a larger size. Both of those read
    // off "this group has no label", so the primary set losing its label would silently demote it.
    it('keeps the primary group unlabelled and every later group labelled', () => {
        expect(NAV_GROUPS[0].label).toBeUndefined();
        expect(NAV_GROUPS.slice(1).map((g) => g.label)).toEqual(['Access', 'Modules', 'System']);
    });

    it('puts the four everyday destinations in the primary group, in the order the design shows', () => {
        expect(NAV_GROUPS[0].items.map((i) => i.title)).toEqual([
            'Overview',
            'Entries',
            'Content types',
            'Workflows',
        ]);
    });

    // A metric is an identifier the rail resolves, so a typo would render nothing and look like a
    // count that had no source. Pinning the set is what makes that a failing test rather than a
    // blank space nobody notices.
    it('names a metric only on the items that have a source for one', () => {
        const withMetric = NAV_GROUPS.flatMap((g) => g.items).filter((i) => i.metric);
        expect(withMetric.map((i) => [i.title, i.metric, i.tone ?? null])).toEqual([
            ['Entries', 'entries', null],
            ['Content types', 'contentTypes', null],
            ['Workflows', 'workflows', null],
            ['Email events', 'recentBounces', 'warning'],
            ['Errors', 'unresolvedErrors', 'danger'],
        ]);
    });

    it('treats a signed-out or role-less user as having no roles', () => {
        // Asserted exactly rather than as "fewer than SuperAdmin". The loose form passes on any
        // filter that removes something, including one that removes the wrong things, and it does
        // not say what a role-less caller should still be offered. Overview and Health are the two
        // destinations with no roles declared, so they are the whole expected set.
        expect(titles(undefined)).toEqual(['Overview', 'Health']);
        expect(titles([])).toEqual(['Overview', 'Health']);
    });
});
