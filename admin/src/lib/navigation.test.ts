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

    // The nav offered Editor the content types screen long after #373 removed that grant from
    // GET /api/content-types, so the link was rendered and the API answered 403. Asserted as a
    // role the server has never heard of, because that is what Editor now is: nothing creates it.
    //
    // Derived from NAV_GROUPS rather than written out. Naming three destinations passes just as
    // happily if Editor is later granted Workflows or API keys, so the test would have gone on
    // reporting the contract it names while a new gated screen leaked. Overview and Health carry no
    // roles and are shown to everyone on purpose, so the assertion is about the gated ones.
    it('routes an unknown role to no gated destination', () => {
        const gated = NAV_GROUPS.flatMap((g) =>
            g.items.filter((i) => i.roles && i.roles.length > 0).map((i) => i.title),
        );

        expect(gated.length).toBeGreaterThan(0);

        const seen = titles(['Editor']);

        expect(seen.filter((title) => gated.includes(title))).toEqual([]);
        expect(seen.length).toBeGreaterThan(0, 'an empty nav would satisfy the line above');
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

    it('treats a signed-out or role-less user as having no roles', () => {
        // Asserted exactly rather than as "fewer than SuperAdmin". The loose form passes on any
        // filter that removes something, including one that removes the wrong things, and it does
        // not say what a role-less caller should still be offered. Overview and Health are the two
        // destinations with no roles declared, so they are the whole expected set.
        expect(titles(undefined)).toEqual(['Overview', 'Health']);
        expect(titles([])).toEqual(['Overview', 'Health']);
    });
});
