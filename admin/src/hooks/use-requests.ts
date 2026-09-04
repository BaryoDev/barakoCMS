'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type Paginated } from '@/lib/api';

/** The methods the API allows. An allowlist on the server too, so this is a copy, not the gate. */
export const REQUEST_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'] as const;

export const SUCCESS_RULES = [
  {
    value: 'TwoHundredRange',
    label: 'Any 2xx',
    description: 'The usual one. A 2xx means it worked.',
  },
  {
    value: 'TwoHundredAndJsonPathAbsent',
    label: '2xx, and a path is absent from the body',
    description: 'For a provider that answers 200 with the error in the body. Needs the path below.',
  },
  {
    value: 'AnyResponse',
    label: 'Any response at all',
    description: 'For a provider whose status codes mean nothing useful.',
  },
] as const;

/** The rule that needs SuccessJsonPath filled in. The server refuses it empty rather than defaulting. */
const RULE_NEEDING_PATH = 'TwoHundredAndJsonPathAbsent';

/** Same shape as the server's slug check, so the form refuses what the API would refuse. */
const SLUG = /^[a-z0-9][a-z0-9-]{0,62}$/;

/**
 * The `{{name}}` holes a template can carry, matching the composer's own regex.
 *
 * Copied deliberately rather than approximated: a looser pattern here would report a variable the
 * server will not substitute, and a tighter one would miss one it will.
 */
const HOLE = /\{\{\s*([A-Za-z0-9_.[\]]+)\s*\}\}/g;

export interface RequestDefinition {
  id: string;
  name: string;
  slug: string;
  connectorSlug: string;
  method: string;
  pathTemplate: string;
  headerTemplates: Record<string, string>;
  bodyTemplate: string | null;
  bodyContentType: string;
  querySlug: string | null;
  success: string;
  successJsonPath: string | null;
  createdAt: string;
  updatedAt: string;
}

/**
 * What the form holds. The same fields the save endpoint takes, all of them strings or maps.
 *
 * `querySlug` is here without an input for it. The save endpoint upserts on the slug and assigns
 * every field it was given, so a draft that leaves the field out saves null over whatever the
 * definition had. The screen shows it and posts it back unchanged.
 */
export interface RequestDraft {
  name: string;
  slug: string;
  connectorSlug: string;
  method: string;
  pathTemplate: string;
  headerTemplates: Record<string, string>;
  bodyTemplate: string;
  bodyContentType: string;
  querySlug: string;
  success: string;
  successJsonPath: string;
}

/**
 * An existing definition loaded back into the form.
 *
 * Every field the save endpoint assigns has to come out of here, including the ones this screen has
 * no input for: the endpoint upserts on the slug and assigns unconditionally, so a field the draft
 * drops is a field the next save nulls.
 */
export function toDraft(request: RequestDefinition): RequestDraft {
  return {
    name: request.name,
    slug: request.slug,
    connectorSlug: request.connectorSlug,
    method: request.method,
    pathTemplate: request.pathTemplate,
    headerTemplates: request.headerTemplates,
    bodyTemplate: request.bodyTemplate ?? '',
    bodyContentType: request.bodyContentType,
    querySlug: request.querySlug ?? '',
    success: request.success,
    successJsonPath: request.successJsonPath ?? '',
  };
}

/**
 * What a dry run answers with.
 *
 * `wouldSend` is a claim about a call that has not happened. When it is false the composer refused
 * and `method`, `url`, `headers` and `body` come back empty, so a screen that renders them without
 * reading the verdict shows an empty request rather than a reason.
 */
export interface DryRunResult {
  wouldSend: boolean;
  refusal: string | null;
  method: string;
  url: string;
  headers: Record<string, string>;
  body: string | null;
}

/** Enough of a connector to offer it in a picker. The API never returns a secret value. */
export interface ConnectorOption {
  slug: string;
  name: string;
  baseUrl: string;
  enabled: boolean;
}

/** Enough of an entry to name it in a picker. */
export interface EntrySummary {
  id: string;
  contentType: string;
  data: Record<string, unknown>;
}

export function useRequests() {
  return useQuery({
    queryKey: ['requests', 'list'],
    queryFn: async () => {
      const response = await api.get<Paginated<RequestDefinition>>('/api/requests');
      return response.data;
    },
  });
}

/** The connectors a request may name. Used to fill the picker and to warn about a disabled one. */
export function useConnectorOptions() {
  return useQuery({
    queryKey: ['connectors', 'options'],
    queryFn: async () => {
      const response = await api.get<Paginated<ConnectorOption>>('/api/connectors');
      return response.data.items;
    },
  });
}

/** Create and update are one endpoint: it upserts on the slug. */
export function useSaveRequest() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (draft: RequestDraft) => {
      const response = await api.post<RequestDefinition>('/api/requests', {
        ...draft,
        // Empty means "no body" rather than "a body that is the empty string": the server treats
        // null as nothing to send, and posting "" would set a Content-Type with nothing under it.
        bodyTemplate: draft.bodyTemplate.length > 0 ? draft.bodyTemplate : null,
        successJsonPath: draft.successJsonPath.length > 0 ? draft.successJsonPath : null,
        querySlug: draft.querySlug.length > 0 ? draft.querySlug : null,
      });
      return response.data;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['requests'] });
    },
  });
}

export function useDeleteRequest() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async (slug: string) => {
      await api.delete(`/api/requests/${encodeURIComponent(slug)}`);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['requests'] });
    },
  });
}

/**
 * Composes the call against one entry and returns it, without sending it.
 *
 * A mutation rather than a query because the route is a POST and because it should run when someone
 * asks, not when a component mounts. Nothing is stored and nothing leaves the server.
 */
export function useDryRunRequest() {
  return useMutation({
    mutationFn: async (input: { slug: string; contentId: string }) => {
      const response = await api.post<DryRunResult>(
        `/api/requests/${encodeURIComponent(input.slug)}/dry-run/${encodeURIComponent(input.contentId)}`,
      );
      return response.data;
    },
  });
}

/**
 * Reads a `Name: value` block into the map the API takes.
 *
 * A textarea rather than a row of paired inputs, because a header block is something operators
 * already have written down somewhere and paste. The cost is that it has to be parsed, and the
 * parse has to refuse rather than drop: a line the parser cannot read, silently ignored, is a header
 * the operator believes they set.
 */
export function parseHeaderTemplates(text: string): {
  headers: Record<string, string>;
  problem: string | null;
} {
  const headers: Record<string, string> = {};
  const seen = new Set<string>();
  const lines = text.split('\n');

  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i].trim();
    if (line.length === 0) continue;

    const colon = line.indexOf(':');
    if (colon < 0) {
      return { headers: {}, problem: `Line ${i + 1} is not "Name: value".` };
    }

    const name = line.slice(0, colon).trim();
    // Split on the first colon only. A value is often a URL, and splitting on every colon would
    // truncate one at its scheme.
    const value = line.slice(colon + 1).trim();

    if (name.length === 0) {
      return { headers: {}, problem: `Line ${i + 1} has no header name.` };
    }

    const key = name.toLowerCase();
    if (seen.has(key)) {
      // Header names are case insensitive, so two spellings are one header. Last-one-wins would
      // silently drop the line above, which is the kind of thing found later by a provider.
      return { headers: {}, problem: `'${name}' is set twice, on line ${i + 1}.` };
    }

    seen.add(key);
    headers[name] = value;
  }

  return { headers, problem: null };
}

/** The inverse, for loading an existing definition back into the textarea. */
export function formatHeaderTemplates(headers: Record<string, string>): string {
  return Object.entries(headers)
    .map(([name, value]) => `${name}: ${value}`)
    .join('\n');
}

/**
 * Every variable the draft's templates name, once each, in the order they are read.
 *
 * Path first, then headers, then body, which is the order the composer substitutes them in, so a
 * list of them reads as the composer's own view of the definition.
 */
export function templateVariables(draft: RequestDraft): string[] {
  const found: string[] = [];
  const seen = new Set<string>();
  const sources = [draft.pathTemplate, ...Object.values(draft.headerTemplates), draft.bodyTemplate];

  for (const source of sources) {
    for (const match of source.matchAll(HOLE)) {
      const name = match[1];
      if (seen.has(name)) continue;
      seen.add(name);
      found.push(name);
    }
  }

  return found;
}

/**
 * The variables the composer will refuse rather than substitute.
 *
 * Named queries exist, but the composer cannot read one into a template yet (#328), so it refuses
 * the whole request rather than posting the literal text to a third party. Saying so on the form is
 * the difference between a warning and a dry run that fails for a reason nobody expected.
 */
export function unresolvableVariables(names: string[]): string[] {
  return names.filter((name) => name.toLowerCase().startsWith('query.'));
}

/**
 * Why this draft cannot be saved, or null.
 *
 * The same checks the save endpoint makes. The server is still the one that enforces them; this is
 * so the button can say what is wrong before a round trip, and so the message is the same one.
 */
export function checkDraft(draft: RequestDraft): string | null {
  if (draft.name.trim().length === 0) return 'Give it a name.';
  if (!SLUG.test(draft.slug)) return 'The slug must be lowercase letters, digits and hyphens.';
  if (!SLUG.test(draft.connectorSlug)) return 'Choose a connector.';

  if (!REQUEST_METHODS.some((method) => method === draft.method)) {
    return `The method must be one of: ${REQUEST_METHODS.join(', ')}.`;
  }

  if (!SUCCESS_RULES.some((rule) => rule.value === draft.success)) {
    return 'Choose how success is decided.';
  }

  if (draft.success === RULE_NEEDING_PATH && draft.successJsonPath.trim().length === 0) {
    // Refused rather than quietly behaving like "any 2xx". Someone who picked this rule has a
    // provider that lies about status codes, and a rule that does nothing lets it keep lying.
    return 'That success rule needs a JSON path that has to be absent.';
  }

  return null;
}

/**
 * A composed body laid out for reading, when it is JSON and it parses.
 *
 * Returned unchanged otherwise. A body that will not parse is exactly what a dry run is for, so it
 * has to survive to the screen as the server composed it rather than being hidden behind an error.
 */
export function prettyBody(body: string | null, contentType: string): string {
  if (body === null || body.length === 0) return '';
  if (!contentType.toLowerCase().includes('json')) return body;

  try {
    return JSON.stringify(JSON.parse(body), null, 2);
  } catch {
    return body;
  }
}

/**
 * How to name an entry in the picker.
 *
 * Falls back to the id when nothing readable is set, rather than to an empty option. Two entries can
 * share a title, so the screen shows the chosen id as well: this only has to be recognisable.
 */
export function describeEntry(entry: EntrySummary): string {
  for (const field of ['Title', 'Name', 'Slug']) {
    const value = entry.data[field];
    if (typeof value === 'string' && value.trim().length > 0) {
      return `${entry.contentType}: ${value.trim()}`;
    }
  }

  return `${entry.contentType}: ${entry.id}`;
}
