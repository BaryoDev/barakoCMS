import { describe, it, expect } from 'vitest';
import { cn } from './utils';

describe('utils', () => {
    describe('cn', () => {
        it('should merge class names correctly', () => {
            expect(cn('c1', 'c2')).toBe('c1 c2');
        });

        it('should handle conditional classes', () => {
            expect(cn('c1', true && 'c2', false && 'c3')).toBe('c1 c2');
        });

        it('should merge tailwind classes properly', () => {
            expect(cn('p-4 p-2')).toBe('p-2');
            expect(cn('text-red-500', 'text-blue-500')).toBe('text-blue-500');
        });
    });
});
