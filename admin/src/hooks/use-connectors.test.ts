import { describe, expect, it } from 'vitest';
import {
    CONNECTOR_AUTH_MODES,
    authModeFor,
    configGap,
    probeOutcome,
    slugify,
    toSecretsPayload,
    type Connector,
} from './use-connectors';

function connector(overrides: Partial<Connector> = {}): Connector {
    return {
        id: '11111111-1111-1111-1111-111111111111',
        name: 'Company Jira',
        slug: 'company-jira',
        auth: 'None',
        baseUrl: 'https://jira.example.com',
        settings: {},
        secretKeys: [],
        enabled: true,
        probePath: '/',
        lastTestedAt: null,
        lastTestResult: null,
        createdAt: '2026-09-01T00:00:00Z',
        updatedAt: '2026-09-01T00:00:00Z',
        ...overrides,
    };
}

describe('toSecretsPayload', () => {
    it('sends a credential the operator typed', () => {
        expect(toSecretsPayload({ Token: 'abc123' })).toEqual({ Token: 'abc123' });
    });

    it('omits a blank box, because a blank box means the stored credential is unchanged', () => {
        // The API never returns a stored secret, so the box renders blank every time the form opens.
        // Sending it as an empty value would delete the token every time somebody corrected the
        // base URL, which is the whole reason absent and empty mean different things on the server.
        expect(toSecretsPayload({ Token: '' })).toBeUndefined();
    });

    it('treats a whitespace-only box as unchanged rather than as a delete', () => {
        // The server's own test is IsNullOrWhiteSpace, so "   " would delete the credential.
        expect(toSecretsPayload({ Token: '   ' })).toBeUndefined();
    });

    it('trims a pasted value, since a trailing newline is a paste artefact and not the credential', () => {
        expect(toSecretsPayload({ Token: '  abc123\n' })).toEqual({ Token: 'abc123' });
    });

    it('sends an empty value only for a credential explicitly cleared', () => {
        expect(toSecretsPayload({}, ['Token'])).toEqual({ Token: '' });
    });

    it('lets a typed replacement win over a request to clear the same credential', () => {
        expect(toSecretsPayload({ Token: 'new-one' }, ['Token'])).toEqual({ Token: 'new-one' });
    });

    it('returns undefined when nothing was typed or cleared, so secrets is left off the body', () => {
        expect(toSecretsPayload({}, [])).toBeUndefined();
    });

    it('keeps one credential and leaves another alone in the same save', () => {
        const payload = toSecretsPayload({ Password: 'hunter2', Token: '' });

        expect(payload).toEqual({ Password: 'hunter2' });
        expect(payload && 'Token' in payload).toBe(false);
    });
});

describe('probeOutcome', () => {
    it('reports untested when the server has never recorded a probe', () => {
        expect(probeOutcome(connector())).toBe('untested');
    });

    it('reads a 2xx out of the stored description as a success', () => {
        expect(probeOutcome(connector({ lastTestResult: 'HTTP 200 in 34 ms' }))).toBe('succeeded');
        expect(probeOutcome(connector({ lastTestResult: 'HTTP 204 in 8 ms' }))).toBe('succeeded');
    });

    it('reports a 3xx as failed, matching IsSuccessStatusCode on the server', () => {
        // The server decided Succeeded with HttpResponseMessage.IsSuccessStatusCode, which is 200 to
        // 299. A 302 to a login page is the case probePath exists to fix, so calling it a success
        // here would hide it.
        expect(probeOutcome(connector({ lastTestResult: 'HTTP 302 in 12 ms' }))).toBe('failed');
    });

    it('reports a 401 and a 500 as failed', () => {
        expect(probeOutcome(connector({ lastTestResult: 'HTTP 401 in 40 ms' }))).toBe('failed');
        expect(probeOutcome(connector({ lastTestResult: 'HTTP 500 in 90 ms' }))).toBe('failed');
    });

    it('reports a probe that never got a status code as failed', () => {
        expect(probeOutcome(connector({ lastTestResult: 'The request timed out. after 2000 ms' }))).toBe(
            'failed',
        );
    });

    it('reports an empty description as untested rather than failed', () => {
        expect(probeOutcome(connector({ lastTestResult: '' }))).toBe('untested');
        expect(probeOutcome(connector({ lastTestResult: '   ' }))).toBe('untested');
    });
});

describe('slugify', () => {
    it('lowercases and joins words with hyphens', () => {
        expect(slugify('Company Jira')).toBe('company-jira');
    });

    it('drops punctuation the server would refuse', () => {
        expect(slugify("Bob's Twilio (prod)")).toBe('bob-s-twilio-prod');
    });

    it('does not start or end with a hyphen, which the server pattern refuses', () => {
        expect(slugify('  Jira!  ')).toBe('jira');
    });

    it('cuts a long name at the 63 characters the server accepts', () => {
        const slug = slugify('a'.repeat(70));

        expect(slug).toHaveLength(63);
        expect(slug).toMatch(/^[a-z0-9][a-z0-9-]{0,62}$/);
    });

    it('does not leave the hyphen the cut landed on at the end', () => {
        // 62 a's, a hyphen, then a word: the 63 character cut falls exactly on the hyphen, and a
        // slug ending in one is refused by the server pattern.
        const slug = slugify('a'.repeat(62) + ' tail');

        expect(slug).toBe('a'.repeat(62));
        expect(slug).toMatch(/^[a-z0-9][a-z0-9-]{0,62}$/);
    });

    it('returns nothing when a name has no usable characters, so the form asks for a slug', () => {
        expect(slugify('!!!')).toBe('');
    });

    it('produces a slug the server pattern accepts for every plausible name', () => {
        const names = ['Company Jira', 'Twilio SMS', 'ACME  --  prod', 'x9'];

        expect(names).toHaveLength(4);
        for (const name of names) {
            expect(slugify(name)).toMatch(/^[a-z0-9][a-z0-9-]{0,62}$/);
        }
    });
});

describe('configGap', () => {
    it('is silent for a connector that needs no credential', () => {
        expect(configGap(connector({ auth: 'None' }))).toBeNull();
    });

    it('names the missing credential for a bearer token connector', () => {
        expect(configGap(connector({ auth: 'BearerToken' }))).toContain('Token');
    });

    it('is silent once the credential is stored', () => {
        expect(configGap(connector({ auth: 'BearerToken', secretKeys: ['Token'] }))).toBeNull();
    });

    it('names the missing setting before the missing credential for basic auth', () => {
        const gap = configGap(connector({ auth: 'Basic' }));

        expect(gap).toContain('Username');
    });

    // The Basic arm of ConnectorSender.TryAttachAuthAsync reads the username with
    // GetValueOrDefault(...) ?? string.Empty and only returns early when the password is missing,
    // so a blank username is sent as base64(":password") and the provider answers 401. Saying it is
    // refused here would send the operator hunting for a client-side refusal that does not exist.
    it('does not claim a missing basic username is refused before the call is sent', () => {
        const gap = configGap(connector({ auth: 'Basic', secretKeys: ['Password'] }));

        expect(gap).toContain('empty username');
        expect(gap).not.toContain('refused');
    });

    // The ApiKeyHeader arm does return early, so this one is a refusal and says so.
    it('says a missing header name is refused before the call is sent', () => {
        const gap = configGap(connector({ auth: 'ApiKeyHeader', secretKeys: ['ApiKey'] }));

        expect(gap).toContain('HeaderName');
        expect(gap).toContain('refused before it is sent');
    });

    // Same for the credential itself, on either mode.
    it('says a missing credential is refused before the call is sent', () => {
        expect(configGap(connector({ auth: 'BearerToken' }))).toContain(
            'refused before it is sent',
        );
    });

    it('is silent for basic auth once the username and the password are both set', () => {
        expect(
            configGap(
                connector({ auth: 'Basic', settings: { Username: 'bot' }, secretKeys: ['Password'] }),
            ),
        ).toBeNull();
    });

    it('treats a blank setting as missing', () => {
        expect(
            configGap(
                connector({ auth: 'Basic', settings: { Username: '  ' }, secretKeys: ['Password'] }),
            ),
        ).toContain('Username');
    });

    it('reports that the sender refuses OAuth2 client credentials outright', () => {
        const gap = configGap(
            connector({ auth: 'OAuth2ClientCredentials', secretKeys: ['ClientSecret'] }),
        );

        // Stored credential and all, the sender refuses this mode, so a stored ClientSecret is not
        // enough to make it work.
        expect(gap).toContain('refuses this mode');
    });

    it('says nothing about an auth mode this build does not recognise', () => {
        expect(configGap(connector({ auth: 'MutualTls' }))).toBeNull();
    });
});

describe('CONNECTOR_AUTH_MODES', () => {
    it('names the credentials the sender actually looks for', () => {
        expect(CONNECTOR_AUTH_MODES).toHaveLength(5);

        // Copied from ConnectorSecretKeys. A mismatch gives a connector that saves, looks
        // configured, and fails every call with "no Token secret is stored".
        expect(authModeFor('BearerToken')?.secretKey).toBe('Token');
        expect(authModeFor('Basic')?.secretKey).toBe('Password');
        expect(authModeFor('ApiKeyHeader')?.secretKey).toBe('ApiKey');
        expect(authModeFor('OAuth2ClientCredentials')?.secretKey).toBe('ClientSecret');
        expect(authModeFor('None')?.secretKey).toBeNull();
    });

    it('names the settings the sender reads, copied from ConnectorSettingKeys', () => {
        expect(authModeFor('Basic')?.settings.map((s) => s.key)).toEqual(['Username']);
        expect(authModeFor('ApiKeyHeader')?.settings.map((s) => s.key)).toEqual(['HeaderName']);
    });

    it('has no mode declaring a settings key of an unexpected shape', () => {
        const keys = CONNECTOR_AUTH_MODES.flatMap((mode) => mode.settings.map((s) => s.key));

        expect(keys).toHaveLength(2);
        for (const key of keys) expect(key).toMatch(/^[A-Z][A-Za-z]+$/);
    });

    it('returns undefined for a mode name that is not in the table', () => {
        expect(authModeFor('MutualTls')).toBeUndefined();
    });
});
