'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/hooks/use-auth';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

const navItems = [
    { href: '/', label: 'Dashboard', icon: '📊' },
    { href: '/schemas', label: 'Content Types', icon: '📝' },
    { href: '/content', label: 'Content', icon: '📄' },
    { href: '/workflows', label: 'Workflows', icon: '⚡' },
    { href: '/roles', label: 'Roles', icon: '🔒' },
    { href: '/users', label: 'Users', icon: '👥' },
    { href: '/ops/health', label: 'Health', icon: '🩺' },
    { href: '/ops/backups', label: 'Backups', icon: '💾' },
    { href: '/ops/logs', label: 'Logs', icon: '📋' },
    { href: '/settings', label: 'Settings', icon: '⚙️' },
];

export function Header() {
    const pathname = usePathname();
    const { logout } = useAuth();

    return (
        <header className="border-b border-slate-800 bg-slate-900/80 backdrop-blur-xl sticky top-0 z-50">
            <div className="container mx-auto px-4 h-16 flex items-center justify-between">
                <div className="flex items-center gap-8">
                    <Link href="/" className="flex items-center gap-3">
                        <div className="w-8 h-8 bg-gradient-to-br from-amber-500 to-orange-600 rounded-lg flex items-center justify-center">
                            <span className="text-lg font-bold text-white">B</span>
                        </div>
                        <span className="text-xl font-semibold text-white">BarakoCMS</span>
                    </Link>


                    <nav className="hidden md:flex items-center gap-1">
                        {navItems.map((item) => (
                            <Link
                                key={item.href}
                                href={item.href}
                                className={cn(
                                    'px-3 py-2 rounded-lg text-sm font-medium transition-colors flex items-center gap-2',
                                    pathname === item.href
                                        ? 'bg-slate-800 text-white'
                                        : 'text-slate-400 hover:text-white hover:bg-slate-800/50'
                                )}
                            >
                                <span>{item.icon}</span>
                                {item.label}
                            </Link>
                        ))}
                    </nav>
                </div>

                <Button
                    variant="outline"
                    onClick={logout}
                    className="border-slate-700 text-slate-300 hover:bg-slate-800"
                >
                    Sign out
                </Button>
            </div>
        </header>
    );
}
