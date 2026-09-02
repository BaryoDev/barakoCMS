import { describe, it, expect } from 'vitest';
import { ContentStatus, STATUS_META, statusMeta } from './content';

describe('content status metadata', () => {
    /**
     * The map has to cover the enum, and TypeScript only half enforces that.
     *
     * `Record<ContentStatus, ...>` catches a missing key at compile time, so this is not about the
     * map. It is about the enum staying in step with the server: Scheduled arrived as a fourth
     * member in 4.0, and a status the server sends that this file has never heard of renders through
     * the fallback as a plain grey badge with the raw string in it. That is the correct behaviour and
     * it is not a substitute for knowing about the status.
     */
    it('has a badge for every status the enum declares', () => {
        const statuses = Object.values(ContentStatus);

        expect(statuses).toHaveLength(4);
        expect(statuses).toContain(ContentStatus.Scheduled);

        for (const status of statuses) {
            expect(STATUS_META[status]).toBeDefined();
            expect(STATUS_META[status].label).not.toBe('');
        }
    });

    it('labels a scheduled entry as scheduled rather than as a draft', () => {
        // It is a draft with a date underneath, and calling it one is what the server-side status
        // exists to stop. See DECISIONS.md D12.
        expect(statusMeta(ContentStatus.Scheduled).label).toBe('Scheduled');
        expect(statusMeta(ContentStatus.Draft).label).toBe('Draft');
    });

    it('does not invent a status the server did not send', () => {
        // Falling back to Draft would render an unknown value as a genuine draft, with a warning
        // badge, indistinguishable from a real one.
        expect(statusMeta(undefined).label).toBe('Unknown');
        expect(statusMeta('Whatever').label).toBe('Whatever');
        expect(statusMeta('Whatever').tone).toBe('muted');
    });
});
