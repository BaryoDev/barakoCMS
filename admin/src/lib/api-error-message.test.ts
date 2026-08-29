import { AxiosError, AxiosHeaders } from 'axios';
import { describe, expect, it } from 'vitest';
import { apiErrorMessage } from './api';

/**
 * The server sends RFC7807 ProblemDetails, whose entries carry `name` and `reason`.
 *
 * apiErrorMessage read `message` and fell back to the entry object itself, so every validation
 * failure rendered as "[object Object]" instead of the server's text. That included the failed
 * login on the sign-in page, which is the first error most people ever see from this product.
 */
function problemDetails(errors: unknown, status = 400) {
    const error = new AxiosError('Request failed', 'ERR_BAD_REQUEST');
    error.response = {
        data: { errors },
        status,
        statusText: '',
        headers: {},
        config: { headers: new AxiosHeaders() },
    };
    return error;
}

describe('apiErrorMessage', () => {
    it('reads the reason from a ProblemDetails entry', () => {
        const error = problemDetails([{ name: 'generalErrors', reason: 'Invalid credentials' }]);

        expect(apiErrorMessage(error)).toBe('Invalid credentials');
    });

    it('joins several ProblemDetails entries', () => {
        const error = problemDetails([
            { name: 'contentType', reason: 'ContentType is required' },
            { name: 'data', reason: 'Data is required' },
        ]);

        expect(apiErrorMessage(error)).toBe('ContentType is required, Data is required');
    });

    it('never renders an entry as [object Object]', () => {
        const error = problemDetails([{ name: 'field', someOtherShape: 'surprise' }]);

        expect(apiErrorMessage(error)).not.toContain('[object Object]');
    });

    it('falls back rather than returning an empty string when nothing is readable', () => {
        const error = problemDetails([{}]);

        expect(apiErrorMessage(error, 'Something went wrong')).toBe('Something went wrong');
    });

    it('still handles a plain string array', () => {
        const error = problemDetails(['Invalid credentials']);

        expect(apiErrorMessage(error)).toBe('Invalid credentials');
    });

    // Auth failures moved from 400 to 401 in 4.0. apiErrorMessage falls back to
    // "Your session has expired" on a 401 with nothing readable in it, so the server's reason has
    // to win, or the most visible error in the product becomes a misleading one.
    it('prefers the server reason over the session-expired fallback on a 401', () => {
        const error = problemDetails([{ name: 'generalErrors', reason: 'Invalid credentials' }], 401);

        expect(apiErrorMessage(error)).toBe('Invalid credentials');
    });

    it('still falls back to session-expired on a 401 with no body', () => {
        const error = new AxiosError('Unauthorized', 'ERR_BAD_REQUEST');
        error.response = {
            data: undefined,
            status: 401,
            statusText: '',
            headers: {},
            config: { headers: new AxiosHeaders() },
        };

        expect(apiErrorMessage(error)).toBe('Your session has expired. Sign in again.');
    });
});
