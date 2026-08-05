'use client';

import { useEffect } from 'react';
import { reportClientError, flushClientErrors } from '@/lib/client-errors';

/**
 * Root error boundary. React render errors do not surface through window.onerror, so without this they
 * would never reach the Diagnostics endpoint. Next.js replaces the whole document when this renders, so
 * it must supply its own <html>/<body> and cannot rely on the app's providers or styles.
 */
export default function GlobalError({
    error,
    reset,
}: {
    error: Error & { digest?: string };
    reset: () => void;
}) {
    useEffect(() => {
        reportClientError({
            kind: 'react',
            message: error.message || 'Render error',
            stack: error.stack,
            // The digest is how a server-side render error is correlated back to the server log.
            source: error.digest ? `digest:${error.digest}` : undefined,
        });
        // The boundary often precedes a reload, so don't wait for the debounce.
        void flushClientErrors();
    }, [error]);

    return (
        <html lang="en">
            <body
                style={{
                    fontFamily: 'system-ui, sans-serif',
                    display: 'flex',
                    minHeight: '100vh',
                    alignItems: 'center',
                    justifyContent: 'center',
                    margin: 0,
                    padding: '2rem',
                }}
            >
                <div style={{ maxWidth: '32rem', textAlign: 'center' }}>
                    <h1 style={{ fontSize: '1.25rem', fontWeight: 600, marginBottom: '0.5rem' }}>
                        Something broke
                    </h1>
                    <p style={{ color: '#666', marginBottom: '1.5rem' }}>
                        The error was reported. You can try again, and it will show up under Errors in the admin.
                    </p>
                    <button
                        onClick={reset}
                        style={{
                            padding: '0.5rem 1rem',
                            borderRadius: '0.375rem',
                            border: '1px solid #ccc',
                            background: '#fff',
                            cursor: 'pointer',
                        }}
                    >
                        Try again
                    </button>
                </div>
            </body>
        </html>
    );
}
