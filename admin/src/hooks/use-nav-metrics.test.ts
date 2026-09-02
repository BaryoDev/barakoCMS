import { describe, it, expect } from 'vitest';
import { countRecentBounces, BOUNCE_LIMIT } from './use-nav-metrics';

const NOW = Date.parse('2026-09-01T12:00:00Z');
const hoursAgo = (h: number) => ({ at: new Date(NOW - h * 3600_000).toISOString() });

describe('recent bounce count', () => {
    // The control. Without it, a window that excludes everything passes the next test.
    it('counts a bounce inside the window', () => {
        expect(countRecentBounces([hoursAgo(1), hoursAgo(23)], NOW)).toBe(2);
    });

    it('excludes a bounce older than the window', () => {
        expect(countRecentBounces([hoursAgo(1), hoursAgo(25), hoursAgo(200)], NOW)).toBe(1);
    });

    it('does not count a row whose timestamp will not parse', () => {
        // Counting it would turn bad data into a badge nobody can clear.
        expect(countRecentBounces([hoursAgo(1), { at: '' }, { at: 'yesterday' }], NOW)).toBe(1);
    });

    it('is zero on an empty list rather than absent', () => {
        // Zero is a real answer here: the endpoint replied and there were none. The rail hides the
        // pill on zero, which is a rendering decision, not this function's.
        expect(countRecentBounces([], NOW)).toBe(0);
    });

    // The cap is what makes the rail render `50+` rather than `50`. If it drifts, the rail starts
    // stating an exact figure it cannot know.
    it('caps the query at fifty rows', () => {
        expect(BOUNCE_LIMIT).toBe(50);
    });
});
