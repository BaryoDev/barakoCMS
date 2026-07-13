// Entries are schemaless JSON, so pick a human label without assuming a field.
// Prefer the conventional title-ish names, then fall back to the first short
// string, and only then to the id — the first key in `data` is often the body.
const TITLE_FIELDS = ['Title', 'Name', 'DisplayName', 'Label', 'Subject', 'Heading'];

export function contentTitle(data: Record<string, unknown>, id: string): string {
    for (const field of TITLE_FIELDS) {
        const value = data[field];
        if (typeof value === 'string' && value.trim()) return value;
    }

    const firstString = Object.values(data).find(
        (value): value is string => typeof value === 'string' && value.trim().length > 0
    );

    return firstString ?? id;
}
