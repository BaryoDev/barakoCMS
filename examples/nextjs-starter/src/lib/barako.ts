export const API_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:5005';

export interface BarakoContent {
    id: string;
    contentType: string;
    data: Record<string, any>; // Flexible for now
    createdAt: string;
    updatedAt: string;
}

export interface Post {
    title: string;
    slug: string;
    excerpt: string;
    body: string;
    coverImage?: string;
    author: string;
    publishedAt: string;
}

export async function fetchContent<T>(contentType: string): Promise<T[]> {
    const res = await fetch(`${API_URL}/api/contents?contentType=${contentType}`, {
        cache: 'no-store', // For demo purposes, always fetch fresh
    });

    if (!res.ok) {
        if (res.status === 404) return [];
        throw new Error(`Failed to fetch ${contentType}: ${res.statusText}`);
    }

    const json = await res.json();
    // BarakoCMS returns { data: [...] } or just [...] depending on endpoint.
    // Assuming standard list endpoint returns array.
    // If your API returns wrapped response, adjust here.
    return json as T[];
}

export async function fetchContentBySlug<T>(contentType: string, slug: string): Promise<T | null> {
    // In a real implementation, we'd use a filter query: ?slug=my-slug
    // For this starter, we fetch all and find (implied simple filtering).
    // Ideally, implemented backend fitlering: /api/contents?contentType=post&data.slug=helloworld

    const all = await fetchContent<T>(contentType);
    // @ts-ignore
    return all.find((item: any) => item.data?.slug === slug || item.slug === slug) || null;
}
