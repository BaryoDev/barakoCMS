import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Header } from './header';

// Mock useAuth hook
vi.mock('@/hooks/use-auth', () => ({
    useAuth: () => ({
        user: { username: 'testuser' },
        logout: vi.fn(),
    }),
}));

// Mock next/navigation
vi.mock('next/navigation', () => ({
    useRouter: () => ({
        push: vi.fn(),
    }),
    usePathname: () => '/',
}));

describe('Header Component', () => {
    it('should render the logo and logout button', () => {
        render(<Header />);

        expect(screen.getByText('BarakoCMS')).toBeInTheDocument();
        expect(screen.getByText('Sign out')).toBeInTheDocument();
    });
});
