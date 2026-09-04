import { beforeEach, describe, expect, it, vi } from 'vitest';
import React from 'react';
import { renderHook, act, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  checkDraft,
  describeEntry,
  formatHeaderTemplates,
  parseHeaderTemplates,
  prettyBody,
  templateVariables,
  toDraft,
  unresolvableVariables,
  useDeleteRequest,
  useDryRunRequest,
  useSaveRequest,
  type RequestDefinition,
  type RequestDraft,
} from './use-requests';
import { api } from '@/lib/api';

vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api');
  return { ...actual, api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } };
});

function draft(overrides: Partial<RequestDraft> = {}): RequestDraft {
  return {
    name: 'Post to the status page',
    slug: 'status-page',
    connectorSlug: 'statuspage',
    method: 'POST',
    pathTemplate: '/v1/incidents',
    headerTemplates: {},
    bodyTemplate: '',
    bodyContentType: 'application/json',
    querySlug: '',
    success: 'TwoHundredRange',
    successJsonPath: '',
    ...overrides,
  };
}

function definition(overrides: Partial<RequestDefinition> = {}): RequestDefinition {
  return {
    id: '9f8c1f4e-0000-4000-8000-000000000001',
    name: 'Post to the status page',
    slug: 'status-page',
    connectorSlug: 'statuspage',
    method: 'POST',
    pathTemplate: '/v1/incidents',
    headerTemplates: { Accept: 'application/json' },
    bodyTemplate: '{"title":"{{Title}}"}',
    bodyContentType: 'application/json',
    querySlug: 'open-incidents',
    success: 'TwoHundredRange',
    successJsonPath: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-02T00:00:00Z',
    ...overrides,
  };
}

function wrapper({ children }: { children: React.ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { mutations: { retry: false }, queries: { retry: false } },
  });
  return React.createElement(QueryClientProvider, { client }, children);
}

describe('parseHeaderTemplates', () => {
  it('reads a name and value off each line', () => {
    const result = parseHeaderTemplates('Accept: application/json\nX-Source: barako');

    expect(result.problem).toBeNull();
    expect(result.headers).toEqual({ Accept: 'application/json', 'X-Source': 'barako' });
  });

  it('splits on the first colon, so a URL value survives', () => {
    const result = parseHeaderTemplates('X-Origin: https://example.test/hooks');

    expect(result.problem).toBeNull();
    expect(result.headers).toEqual({ 'X-Origin': 'https://example.test/hooks' });
  });

  it('skips blank lines rather than calling them a problem', () => {
    const result = parseHeaderTemplates('\nAccept: application/json\n   \n');

    expect(result.problem).toBeNull();
    expect(result.headers).toEqual({ Accept: 'application/json' });
  });

  it('keeps an empty value, which is a header set to nothing rather than an unset header', () => {
    const result = parseHeaderTemplates('X-Trace:');

    expect(result.problem).toBeNull();
    expect(result.headers).toEqual({ 'X-Trace': '' });
  });

  it('refuses a line with no colon instead of dropping it', () => {
    const result = parseHeaderTemplates('Accept: application/json\nX-Source barako');

    // Dropping it would leave the operator believing they set a header they did not.
    expect(result.problem).toBe('Line 2 is not "Name: value".');
    expect(result.headers).toEqual({});
  });

  it('refuses a line with no name', () => {
    const result = parseHeaderTemplates(': barako');

    expect(result.problem).toBe('Line 1 has no header name.');
    expect(result.headers).toEqual({});
  });

  it('refuses the same header twice, whatever the casing', () => {
    // Both directions on purpose. A parser that only lowercases the name it stores still catches
    // 'Accept' then 'accept', because the second spelling happens to match what it stored, and
    // reports nothing for 'Accept' then 'ACCEPT'. Header names are case insensitive, so these are
    // one header either way, and last-one-wins would silently drop the line above.
    expect(parseHeaderTemplates('Accept: application/json\naccept: text/plain')).toEqual({
      headers: {},
      problem: "'accept' is set twice, on line 2.",
    });

    expect(parseHeaderTemplates('Accept: application/json\nACCEPT: text/plain')).toEqual({
      headers: {},
      problem: "'ACCEPT' is set twice, on line 2.",
    });
  });

  it('reads back what it formatted', () => {
    const headers = { Accept: 'application/json', 'X-Source': 'barako' };

    expect(parseHeaderTemplates(formatHeaderTemplates(headers)).headers).toEqual(headers);
  });
});

describe('formatHeaderTemplates', () => {
  it('is empty for no headers, so the textarea starts blank', () => {
    expect(formatHeaderTemplates({})).toBe('');
  });

  it('puts one header on each line', () => {
    expect(formatHeaderTemplates({ Accept: 'application/json', 'X-Source': 'barako' })).toBe(
      'Accept: application/json\nX-Source: barako',
    );
  });
});

describe('templateVariables', () => {
  it('finds the holes in the path, the headers and the body, in that order', () => {
    const names = templateVariables(
      draft({
        pathTemplate: '/v1/posts/{{Slug}}',
        headerTemplates: { 'X-Entry': '{{id}}' },
        bodyTemplate: '{"title": "{{Title}}"}',
      }),
    );

    expect(names).toEqual(['Slug', 'id', 'Title']);
  });

  it('names each variable once even when it is used several times', () => {
    const names = templateVariables(
      draft({
        pathTemplate: '/v1/posts/{{Slug}}',
        bodyTemplate: '{"slug": "{{Slug}}", "again": "{{Slug}}"}',
      }),
    );

    expect(names).toEqual(['Slug']);
  });

  it('accepts the whitespace and the dotted and indexed forms the composer accepts', () => {
    const names = templateVariables(
      draft({ pathTemplate: '/v1/{{ data.Title }}/{{tags[0]}}' }),
    );

    expect(names).toEqual(['data.Title', 'tags[0]']);
  });

  it('returns nothing for templates with no holes', () => {
    expect(templateVariables(draft({ pathTemplate: '/v1/incidents' }))).toEqual([]);
  });

  it('ignores a single brace, which is JSON rather than a hole', () => {
    const names = templateVariables(draft({ bodyTemplate: '{"fixed": "value"}' }));

    expect(names).toEqual([]);
  });
});

describe('unresolvableVariables', () => {
  it('picks out the query variables the composer refuses', () => {
    expect(unresolvableVariables(['Title', 'query.rows', 'id'])).toEqual(['query.rows']);
  });

  it('matches whatever case the operator typed', () => {
    expect(unresolvableVariables(['Query.Rows'])).toEqual(['Query.Rows']);
  });

  it('leaves a variable that merely starts with the word alone', () => {
    // 'queryString' is a field name, not a query reference. The composer keys off the dot.
    expect(unresolvableVariables(['queryString', 'query'])).toEqual([]);
  });

  it('returns nothing for a template that names none', () => {
    expect(unresolvableVariables(['Title', 'publicurl'])).toEqual([]);
  });
});

describe('checkDraft', () => {
  it('passes a draft the API would accept', () => {
    expect(checkDraft(draft())).toBeNull();
  });

  it('wants a name that is not only spaces', () => {
    expect(checkDraft(draft({ name: '   ' }))).toBe('Give it a name.');
  });

  it('refuses a slug the API would refuse', () => {
    for (const slug of ['', 'Status-Page', 'status page', '-status', 'status_page']) {
      expect(checkDraft(draft({ slug }))).toBe(
        'The slug must be lowercase letters, digits and hyphens.',
      );
    }
  });

  it('refuses a request that names no connector', () => {
    expect(checkDraft(draft({ connectorSlug: '' }))).toBe('Choose a connector.');
  });

  it('refuses a method outside the allowlist', () => {
    // TRACE against some proxies echoes the Authorization header the sender attaches, which is a
    // way to read a credential back out of a connector that never returns one.
    expect(checkDraft(draft({ method: 'TRACE' }))).toBe(
      'The method must be one of: GET, POST, PUT, PATCH, DELETE.',
    );
  });

  it('refuses a success rule that is not one of the three', () => {
    expect(checkDraft(draft({ success: 'Whenever' }))).toBe('Choose how success is decided.');
  });

  it('refuses the JSON path rule with no path, rather than letting it mean any 2xx', () => {
    expect(
      checkDraft(draft({ success: 'TwoHundredAndJsonPathAbsent', successJsonPath: '  ' })),
    ).toBe('That success rule needs a JSON path that has to be absent.');
  });

  it('accepts the JSON path rule once a path is given', () => {
    expect(
      checkDraft(draft({ success: 'TwoHundredAndJsonPathAbsent', successJsonPath: 'error.code' })),
    ).toBeNull();
  });
});

describe('prettyBody', () => {
  it('lays out a JSON body', () => {
    expect(prettyBody('{"a":1}', 'application/json')).toBe('{\n  "a": 1\n}');
  });

  it('leaves a JSON body that will not parse exactly as the server composed it', () => {
    // The case a dry run exists for. Reformatting or hiding it would take away the evidence.
    expect(prettyBody('{"a":}', 'application/json')).toBe('{"a":}');
  });

  it('leaves a body that is not JSON alone', () => {
    expect(prettyBody('a=1&b=2', 'application/x-www-form-urlencoded')).toBe('a=1&b=2');
  });

  it('is empty for no body', () => {
    expect(prettyBody(null, 'application/json')).toBe('');
    expect(prettyBody('', 'application/json')).toBe('');
  });

  it('reads the content type case insensitively', () => {
    expect(prettyBody('{"a":1}', 'Application/JSON; charset=utf-8')).toBe('{\n  "a": 1\n}');
  });
});

describe('describeEntry', () => {
  it('names an entry by its title', () => {
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Title: 'Hello' } })).toBe(
      'Post: Hello',
    );
  });

  it('falls back through Name and Slug', () => {
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Name: 'Named' } })).toBe(
      'Post: Named',
    );
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Slug: 'slugged' } })).toBe(
      'Post: slugged',
    );
  });

  it('falls back to the id when nothing readable is set', () => {
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Title: '  ' } })).toBe('Post: e1');
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: {} })).toBe('Post: e1');
  });

  it('ignores a title that is not a string', () => {
    // A number field named Title would otherwise render as "Post: 12" through a toString nobody
    // asked for, or throw on a null.
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Title: 12 } })).toBe('Post: e1');
    expect(describeEntry({ id: 'e1', contentType: 'Post', data: { Title: null } })).toBe('Post: e1');
  });
});

/**
 * The save endpoint upserts on the slug and assigns every field it was given, so a field the form
 * fails to carry is one the next save overwrites with null. That makes the draft's completeness a
 * behaviour rather than a detail, and this is the round trip that pins it.
 */
describe('toDraft', () => {
  it('carries every field the save endpoint assigns, including the ones with no input', () => {
    const source = definition();
    const asDraft = toDraft(source);

    expect(asDraft.querySlug).toBe('open-incidents');
    expect(asDraft).toEqual({
      name: source.name,
      slug: source.slug,
      connectorSlug: source.connectorSlug,
      method: source.method,
      pathTemplate: source.pathTemplate,
      headerTemplates: source.headerTemplates,
      bodyTemplate: source.bodyTemplate,
      bodyContentType: source.bodyContentType,
      querySlug: source.querySlug,
      success: source.success,
      successJsonPath: '',
    });
  });

  it('turns the nullable fields into the empty strings the form holds', () => {
    const asDraft = toDraft(
      definition({ bodyTemplate: null, querySlug: null, successJsonPath: null }),
    );

    expect(asDraft.bodyTemplate).toBe('');
    expect(asDraft.querySlug).toBe('');
    expect(asDraft.successJsonPath).toBe('');
  });
});

/**
 * The hooks, against a mocked transport. What is worth asserting is not that react-query works but
 * the three things this module decides on its own: the exact route, the empty-to-null conversions
 * the server reads as "nothing here", and the invalidation without which the list keeps showing a
 * definition as it was before the save.
 */
describe('useSaveRequest', () => {
  beforeEach(() => {
    vi.mocked(api.post).mockReset();
    vi.mocked(api.post).mockResolvedValue({ data: definition() });
  });

  function saved(input: RequestDraft) {
    const { result } = renderHook(() => useSaveRequest(), { wrapper });
    act(() => result.current.mutate(input));
    return result;
  }

  function postedBody() {
    return vi.mocked(api.post).mock.calls[0][1] as Record<string, unknown>;
  }

  it('posts an empty body template as null rather than an empty string', async () => {
    const result = saved(draft({ bodyTemplate: '' }));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(vi.mocked(api.post).mock.calls[0][0]).toBe('/api/requests');
    expect(postedBody().bodyTemplate).toBeNull();
  });

  it('posts a body template that was filled in unchanged', async () => {
    const result = saved(draft({ bodyTemplate: '{"a":1}' }));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(postedBody().bodyTemplate).toBe('{"a":1}');
  });

  it('posts an empty success path as null rather than an empty string', async () => {
    const result = saved(draft({ successJsonPath: '' }));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(postedBody().successJsonPath).toBeNull();
  });

  it('posts a success path that was filled in unchanged', async () => {
    const result = saved(
      draft({ success: 'TwoHundredAndJsonPathAbsent', successJsonPath: 'error.code' }),
    );
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(postedBody().successJsonPath).toBe('error.code');
  });

  it('posts the query slug back, so a save does not clear one the definition already had', async () => {
    const result = saved(toDraft(definition({ querySlug: 'open-incidents' })));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(postedBody().querySlug).toBe('open-incidents');
  });

  it('posts no query slug as null when the definition never had one', async () => {
    const result = saved(toDraft(definition({ querySlug: null })));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(postedBody().querySlug).toBeNull();
  });

  it('invalidates the request list, so a row shows what was just saved', async () => {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const { result } = renderHook(() => useSaveRequest(), {
      wrapper: ({ children }: { children: React.ReactNode }) =>
        React.createElement(QueryClientProvider, { client }, children),
    });

    act(() => result.current.mutate(draft()));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['requests'] });
  });
});

describe('useDeleteRequest', () => {
  beforeEach(() => {
    vi.mocked(api.delete).mockReset();
    vi.mocked(api.delete).mockResolvedValue({ data: null });
  });

  it('deletes at /api/requests/{slug}', async () => {
    const { result } = renderHook(() => useDeleteRequest(), { wrapper });
    act(() => result.current.mutate('status-page'));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(api.delete).toHaveBeenCalledWith('/api/requests/status-page');
  });

  it('escapes the slug rather than letting it shape the path', async () => {
    const { result } = renderHook(() => useDeleteRequest(), { wrapper });
    act(() => result.current.mutate('a/b'));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(api.delete).toHaveBeenCalledWith('/api/requests/a%2Fb');
  });

  it('invalidates the request list, so a deleted row leaves the table', async () => {
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const { result } = renderHook(() => useDeleteRequest(), {
      wrapper: ({ children }: { children: React.ReactNode }) =>
        React.createElement(QueryClientProvider, { client }, children),
    });

    act(() => result.current.mutate('status-page'));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['requests'] });
  });
});

describe('useDryRunRequest', () => {
  beforeEach(() => {
    vi.mocked(api.post).mockReset();
    vi.mocked(api.post).mockResolvedValue({
      data: {
        wouldSend: true,
        refusal: null,
        method: 'POST',
        url: 'https://example.test/v1',
        headers: {},
        body: null,
      },
    });
  });

  it('posts to the dry run route for the slug and the entry', async () => {
    const { result } = renderHook(() => useDryRunRequest(), { wrapper });
    act(() => result.current.mutate({ slug: 'status-page', contentId: 'entry-1' }));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(api.post).toHaveBeenCalledWith('/api/requests/status-page/dry-run/entry-1');
  });

  it('escapes both segments rather than letting either shape the path', async () => {
    const { result } = renderHook(() => useDryRunRequest(), { wrapper });
    act(() => result.current.mutate({ slug: 'a/b', contentId: 'c/d' }));
    await waitFor(() => expect(result.current.isSuccess).toBe(true));

    expect(api.post).toHaveBeenCalledWith('/api/requests/a%2Fb/dry-run/c%2Fd');
  });
});
