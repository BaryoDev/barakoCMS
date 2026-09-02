'use client';

import { useEffect, useState } from 'react';

/**
 * The value, held back until it has stopped changing for `delay` milliseconds.
 *
 * For a search box that drives a server query. Sending on every keystroke is not only wasteful:
 * the responses can arrive out of order, so the table settles on whichever request happened to
 * finish last rather than on what is in the box. Debouncing does not make that impossible, it
 * makes it rare enough that a person cannot produce it by typing.
 */
export function useDebounced<T>(value: T, delay = 300): T {
    const [settled, setSettled] = useState(value);

    useEffect(() => {
        const timer = setTimeout(() => setSettled(value), delay);

        // Cleared on every change, which is what makes it a debounce rather than a delay: a timer
        // left running would fire with a value the caller has already moved on from.
        return () => clearTimeout(timer);
    }, [value, delay]);

    return settled;
}
