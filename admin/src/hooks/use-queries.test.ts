import { describe, expect, it } from 'vitest';
import {
    cellText,
    hasUnsavedEdits,
    isQuerySlug,
    previewColumns,
    projectableFields,
    refusalFor,
    slugify,
    type QueryDefinition,
    type SaveQueryInput,
} from './use-queries';
import { SensitivityLevel, type ContentTypeDefinition, type FieldDefinition } from '@/types/schema';

function field(name: string, sensitivity?: SensitivityLevel): FieldDefinition {
    return {
        name,
        displayName: name,
        type: 'string',
        isRequired: false,
        ...(sensitivity === undefined ? {} : { sensitivity }),
    };
}

function schema(fields: FieldDefinition[]): ContentTypeDefinition {
    return { name: 'Subscriber', displayName: 'Subscriber', fields };
}

function draft(overrides: Partial<SaveQueryInput> = {}): SaveQueryInput {
    return {
        name: 'Active subscribers',
        slug: 'active-subscribers',
        contentType: 'Subscriber',
        filters: [],
        sortField: null,
        descending: false,
        limit: 100,
        fields: ['Email'],
        ...overrides,
    };
}

function saved(overrides: Partial<QueryDefinition> = {}): QueryDefinition {
    return {
        id: '11111111-1111-1111-1111-111111111111',
        name: 'Active subscribers',
        slug: 'active-subscribers',
        contentType: 'Subscriber',
        filters: [],
        sortField: null,
        descending: false,
        limit: 100,
        fields: ['Email'],
        createdAt: '2026-09-01T00:00:00Z',
        updatedAt: '2026-09-01T00:00:00Z',
        ...overrides,
    };
}

describe('isQuerySlug', () => {
    it('accepts lowercase letters, digits and hyphens after a leading alphanumeric', () => {
        expect(isQuerySlug('active-subscribers')).toBe(true);
        expect(isQuerySlug('a')).toBe(true);
        expect(isQuerySlug('9lives')).toBe(true);
    });

    it('refuses a leading hyphen, uppercase, spaces and the separators a path would eat', () => {
        expect(isQuerySlug('-leading')).toBe(false);
        expect(isQuerySlug('Active')).toBe(false);
        expect(isQuerySlug('two words')).toBe(false);
        expect(isQuerySlug('a/b')).toBe(false);
        expect(isQuerySlug('under_score')).toBe(false);
        expect(isQuerySlug('')).toBe(false);
    });

    it('caps at 63 characters, matching the server regex', () => {
        expect(isQuerySlug('a'.repeat(63))).toBe(true);
        expect(isQuerySlug('a'.repeat(64))).toBe(false);
    });
});

describe('slugify', () => {
    it('produces a slug the server rule accepts', () => {
        const value = slugify('Active Newsletter Subscribers');
        expect(value).toBe('active-newsletter-subscribers');
        expect(isQuerySlug(value)).toBe(true);
    });

    it('collapses punctuation runs and trims the ends rather than leaving a leading hyphen', () => {
        const value = slugify('  (Q3) Subscribers -- EU!  ');
        expect(value).toBe('q3-subscribers-eu');
        expect(isQuerySlug(value)).toBe(true);
    });

    it('does not leave a trailing hyphen after the 63 character cut', () => {
        // 'a' x 62 then a space then more, so the slice lands exactly on the hyphen.
        const value = slugify(`${'a'.repeat(62)} tail`);
        expect(value).toBe('a'.repeat(62));
        expect(isQuerySlug(value)).toBe(true);
    });

    it('returns an empty string when a name has nothing sluggable, so the form asks for one', () => {
        expect(slugify('***')).toBe('');
        expect(isQuerySlug(slugify('***'))).toBe(false);
    });
});

describe('projectableFields', () => {
    it('keeps only Public fields, because the server refuses the others', () => {
        const fields = projectableFields(
            schema([
                field('Email'),
                field('Salary', SensitivityLevel.Sensitive),
                field('Notes', SensitivityLevel.Hidden),
            ]),
        );

        expect(fields).toHaveLength(1);
        expect(fields.map((f) => f.name)).toEqual(['Email']);
    });

    it('treats an absent sensitivity as Public, which is the model default', () => {
        const fields = projectableFields(schema([field('Email'), field('Name')]));

        expect(fields).toHaveLength(2);
        expect(fields.map((f) => f.name)).toEqual(['Email', 'Name']);
    });

    it('returns nothing for a type that has not loaded yet', () => {
        expect(projectableFields(undefined)).toEqual([]);
    });
});

describe('refusalFor', () => {
    const allowed = [field('Email'), field('Status')];

    it('passes a draft the server would accept', () => {
        expect(refusalFor(draft(), allowed)).toBeNull();
    });

    it('refuses a blank name and a name that is only spaces', () => {
        expect(refusalFor(draft({ name: '' }), allowed)).toBe('Name is required.');
        expect(refusalFor(draft({ name: '   ' }), allowed)).toBe('Name is required.');
    });

    it('refuses a slug the server route would not match', () => {
        expect(refusalFor(draft({ slug: 'Not A Slug' }), allowed)).toContain('Slug must start');
    });

    it('refuses an empty projection rather than defaulting to every field', () => {
        // The one rule worth naming twice: "all fields" is how a personal-data field added next
        // year ends up in a payload nobody revisited.
        expect(refusalFor(draft({ fields: [] }), allowed)).toContain('at least one field');
    });

    it('names a field that is not Public on the chosen type', () => {
        expect(refusalFor(draft({ fields: ['Salary'] }), allowed)).toBe(
            "'Salary' cannot be returned from 'Subscriber'.",
        );
    });

    it('names a filter field and a sort field that are not Public', () => {
        expect(refusalFor(draft({ filters: [{ field: 'Salary', op: 'eq', value: '1' }] }), allowed)).toBe(
            "'Salary' cannot be filtered on 'Subscriber'.",
        );
        expect(refusalFor(draft({ sortField: 'Salary' }), allowed)).toBe(
            "'Salary' cannot be sorted on 'Subscriber'.",
        );
    });

    it('accepts a field named in a different case, the way the server compares it', () => {
        expect(refusalFor(draft({ fields: ['email'], sortField: 'STATUS' }), allowed)).toBeNull();
    });

    it('refuses a filter with no field chosen yet', () => {
        expect(refusalFor(draft({ filters: [{ field: '', op: 'eq', value: 'x' }] }), allowed)).toBe(
            'Every filter needs a field.',
        );
    });

    it('refuses more than ten filters', () => {
        const filters = Array.from({ length: 11 }, () => ({
            field: 'Email',
            op: 'eq' as const,
            value: 'x',
        }));

        expect(refusalFor(draft({ filters }), allowed)).toContain('Too many filters');
        expect(refusalFor(draft({ filters: filters.slice(0, 10) }), allowed)).toBeNull();
    });

    it('holds the limit inside the server ceiling, both ends and whole numbers only', () => {
        expect(refusalFor(draft({ limit: 0 }), allowed)).toContain('between 1 and 1000');
        expect(refusalFor(draft({ limit: 1001 }), allowed)).toContain('between 1 and 1000');
        expect(refusalFor(draft({ limit: 1.5 }), allowed)).toContain('between 1 and 1000');
        expect(refusalFor(draft({ limit: Number.NaN }), allowed)).toContain('between 1 and 1000');
        expect(refusalFor(draft({ limit: 1 }), allowed)).toBeNull();
        expect(refusalFor(draft({ limit: 1000 }), allowed)).toBeNull();
    });

    it('refuses a draft with no content type, which has no allowlist to check against', () => {
        expect(refusalFor(draft({ contentType: '' }), [])).toBe('Choose a content type.');
    });
});

describe('hasUnsavedEdits', () => {
    it('is true for a query that has never been saved, so preview stays shut', () => {
        expect(hasUnsavedEdits(draft(), undefined)).toBe(true);
    });

    it('is false when the draft matches the stored copy', () => {
        expect(hasUnsavedEdits(draft(), saved())).toBe(false);
    });

    it('ignores the whitespace the save would trim off the name', () => {
        expect(hasUnsavedEdits(draft({ name: '  Active subscribers  ' }), saved())).toBe(false);
    });

    it('treats a null sort and an empty sort as the same, since the form clears it to one of them', () => {
        expect(hasUnsavedEdits(draft({ sortField: '' }), saved({ sortField: null }))).toBe(false);
    });

    it('notices every part of the definition changing', () => {
        expect(hasUnsavedEdits(draft({ name: 'Renamed' }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ contentType: 'Article' }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ sortField: 'Email' }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ descending: true }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ limit: 50 }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ fields: ['Email', 'Status'] }), saved())).toBe(true);
        expect(hasUnsavedEdits(draft({ fields: ['Status'] }), saved())).toBe(true);
    });

    it('notices a reordered projection, because the preview columns follow that order', () => {
        expect(
            hasUnsavedEdits(
                draft({ fields: ['Status', 'Email'] }),
                saved({ fields: ['Email', 'Status'] }),
            ),
        ).toBe(true);
    });

    it('notices a filter whose value or operator changed, not just one added or removed', () => {
        const stored = saved({ filters: [{ field: 'Status', op: 'eq', value: 'Active' }] });

        expect(
            hasUnsavedEdits(draft({ filters: [{ field: 'Status', op: 'eq', value: 'Active' }] }), stored),
        ).toBe(false);
        expect(
            hasUnsavedEdits(draft({ filters: [{ field: 'Status', op: 'ne', value: 'Active' }] }), stored),
        ).toBe(true);
        expect(
            hasUnsavedEdits(draft({ filters: [{ field: 'Status', op: 'eq', value: 'Lapsed' }] }), stored),
        ).toBe(true);
        expect(
            hasUnsavedEdits(draft({ filters: [{ field: 'Email', op: 'eq', value: 'Active' }] }), stored),
        ).toBe(true);
        expect(hasUnsavedEdits(draft({ filters: [] }), stored)).toBe(true);
    });
});

describe('previewColumns', () => {
    it('orders the columns by the projection, not by what the first row happens to carry', () => {
        const rows = [{ Status: 'Active', Email: 'a@example.com' }];

        expect(previewColumns(['Email', 'Status'], rows)).toEqual([
            { label: 'Email', key: 'Email' },
            { label: 'Status', key: 'Status' },
        ]);
    });

    it('reads a row key that differs in case from the field name, in either direction', () => {
        // The runner projects under the schema's spelling, so a query saved with the field typed as
        // "price" comes back with rows keyed "Price". A column keyed on the wrong case finds
        // nothing and renders every cell blank, which reads as "no data" rather than as a bug.
        const rowUp: Record<string, unknown> = { Price: 12 };
        const up = previewColumns(['price'], [rowUp]);
        expect(up).toEqual([{ label: 'price', key: 'Price' }]);
        expect(cellText(rowUp[up[0].key])).toBe('12');

        const rowDown: Record<string, unknown> = { price: 12 };
        const down = previewColumns(['Price'], [rowDown]);
        expect(down).toEqual([{ label: 'Price', key: 'price' }]);
        expect(cellText(rowDown[down[0].key])).toBe('12');
    });

    it('finds a key that only a later row carries, so a sparse first row does not blank the column', () => {
        const rows = [{ Email: 'a@example.com' }, { Email: 'b@example.com', Status: 'Active' }];

        expect(previewColumns(['Email', 'Status'], rows)).toEqual([
            { label: 'Email', key: 'Email' },
            { label: 'Status', key: 'Status' },
        ]);
    });

    it('still names a column no row carries, so the header shows what was asked for', () => {
        expect(previewColumns(['Email'], [])).toEqual([{ label: 'Email', key: 'Email' }]);
    });

    it('falls back to the row keys, first seen first, when the projection is not to hand', () => {
        const rows = [{ Email: 'a@example.com' }, { Status: 'Active', Email: 'b@example.com' }];

        expect(previewColumns([], rows)).toEqual([
            { label: 'Email', key: 'Email' },
            { label: 'Status', key: 'Status' },
        ]);
    });

    it('has no columns when there is neither a projection nor a row', () => {
        expect(previewColumns([], [])).toEqual([]);
    });
});

describe('cellText', () => {
    it('renders a missing value as blank rather than the word undefined', () => {
        expect(cellText(undefined)).toBe('');
        expect(cellText(null)).toBe('');
    });

    it('renders scalars as themselves, including the falsy ones', () => {
        expect(cellText('hello')).toBe('hello');
        expect(cellText('')).toBe('');
        expect(cellText(0)).toBe('0');
        expect(cellText(false)).toBe('false');
        expect(cellText(true)).toBe('true');
    });

    it('renders a list or an object as JSON, since a projected field can hold either', () => {
        expect(cellText(['a', 'b'])).toBe('["a","b"]');
        expect(cellText({ city: 'Koronadal' })).toBe('{"city":"Koronadal"}');
    });
});
