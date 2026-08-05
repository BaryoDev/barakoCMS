'use client';

import { useEffect } from 'react';
import { installClientErrorHandlers } from '@/lib/client-errors';

/**
 * Installs the browser error listeners for the lifetime of the app. Renders nothing. Mounted once in
 * the root layout so uncaught errors and unhandled rejections reach the Diagnostics endpoint, which is
 * what the admin's Errors page reads.
 */
export default function ErrorReporter() {
    useEffect(() => installClientErrorHandlers(), []);
    return null;
}
