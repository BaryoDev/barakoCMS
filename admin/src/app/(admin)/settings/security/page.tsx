'use client';

import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { toast } from 'sonner';
import { apiErrorMessage } from '@/lib/api';
import { useMfaStatus, useMfaSetup, useMfaEnable, useMfaDisable, type MfaSetup } from '@/hooks/use-mfa';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

/**
 * Two-factor authentication settings. Enrollment is deliberately three explicit steps — scan, confirm,
 * save recovery codes — because the secret and the recovery codes are each shown exactly once and are
 * unrecoverable afterwards.
 */
export default function SecurityPage() {
    const status = useMfaStatus();
    const setup = useMfaSetup();
    const enable = useMfaEnable();
    const disable = useMfaDisable();

    const [pending, setPending] = useState<MfaSetup | null>(null);
    const [qrDataUrl, setQrDataUrl] = useState<string | null>(null);
    const [code, setCode] = useState('');
    const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);
    const [disableCode, setDisableCode] = useState('');

    // Render the otpauth URI locally. The secret must not travel to a third-party QR service.
    useEffect(() => {
        if (!pending) return;
        let cancelled = false;
        QRCode.toDataURL(pending.otpauthUri, { width: 200, margin: 1 })
            .then((url) => {
                if (!cancelled) setQrDataUrl(url);
            })
            .catch(() => {
                // The typed-in secret below is the fallback, so a QR failure isn't fatal.
                if (!cancelled) setQrDataUrl(null);
            });
        return () => {
            cancelled = true;
        };
    }, [pending]);

    const beginSetup = () =>
        setup.mutate(undefined, {
            onSuccess: (data) => {
                // Drop any previous QR so a restarted enrollment can't briefly show the old secret.
                setQrDataUrl(null);
                setPending(data);
                setCode('');
            },
            onError: (error) => toast.error(apiErrorMessage(error, 'Could not start setup.')),
        });

    const confirmSetup = (e: React.FormEvent) => {
        e.preventDefault();
        enable.mutate(code.trim(), {
            onSuccess: (data) => {
                setRecoveryCodes(data.recoveryCodes);
                setPending(null);
                setCode('');
                toast.success('Two-factor authentication is on.');
            },
            onError: (error) => {
                setCode('');
                toast.error(apiErrorMessage(error, 'That code was not accepted. Codes rotate every 30 seconds.'));
            },
        });
    };

    const confirmDisable = (e: React.FormEvent) => {
        e.preventDefault();
        disable.mutate(disableCode.trim(), {
            onSuccess: () => {
                setDisableCode('');
                setRecoveryCodes(null);
                toast.success('Two-factor authentication is off.');
            },
            onError: (error) => {
                setDisableCode('');
                toast.error(apiErrorMessage(error, 'That code was not accepted.'));
            },
        });
    };

    const enabled = status.data?.enabled ?? false;

    return (
        <div className="space-y-6">
            <div>
                <h1 className="font-display text-2xl font-semibold tracking-tight">Security</h1>
                <p className="text-muted-foreground mt-1 text-sm">
                    Add a second step to your sign-in, so a stolen password isn&apos;t enough on its own.
                </p>
            </div>

            <Card>
                <CardHeader>
                    <div className="flex items-center justify-between gap-4">
                        <div>
                            <CardTitle className="text-base">Two-factor authentication</CardTitle>
                            <CardDescription>Codes from an authenticator app such as 1Password or Google Authenticator.</CardDescription>
                        </div>
                        {status.isLoading ? null : (
                            <Badge variant={enabled ? 'default' : 'secondary'}>{enabled ? 'On' : 'Off'}</Badge>
                        )}
                    </div>
                </CardHeader>

                <CardContent className="space-y-6">
                    {/* Shown once, right after enabling. */}
                    {recoveryCodes && (
                        <div className="rounded-md border p-4">
                            <p className="text-sm font-medium">Save your recovery codes</p>
                            <p className="text-muted-foreground mt-1 text-sm">
                                Each one works once, if you lose your authenticator. This is the only time they are shown.
                            </p>
                            <ul className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 font-mono text-sm">
                                {recoveryCodes.map((rc) => (
                                    <li key={rc}>{rc}</li>
                                ))}
                            </ul>
                            <div className="mt-4 flex gap-2">
                                <Button
                                    type="button"
                                    variant="outline"
                                    size="sm"
                                    onClick={() => {
                                        void navigator.clipboard.writeText(recoveryCodes.join('\n'));
                                        toast.success('Recovery codes copied.');
                                    }}
                                >
                                    Copy codes
                                </Button>
                                <Button type="button" variant="ghost" size="sm" onClick={() => setRecoveryCodes(null)}>
                                    I&apos;ve saved them
                                </Button>
                            </div>
                        </div>
                    )}

                    {/* Enrollment in progress. */}
                    {pending && (
                        <form onSubmit={confirmSetup} className="space-y-4">
                            <div className="flex flex-col gap-4 sm:flex-row sm:items-start">
                                {qrDataUrl && (
                                    // eslint-disable-next-line @next/next/no-img-element
                                    <img
                                        src={qrDataUrl}
                                        alt="QR code for your authenticator app"
                                        className="size-[200px] shrink-0 rounded-md border bg-white p-2"
                                    />
                                )}
                                <div className="space-y-2 text-sm">
                                    <p className="font-medium">Scan this with your authenticator app</p>
                                    <p className="text-muted-foreground">
                                        Can&apos;t scan? Enter this key manually:
                                    </p>
                                    <code className="bg-muted block break-all rounded px-2 py-1 font-mono text-xs">
                                        {pending.secret}
                                    </code>
                                </div>
                            </div>

                            <div className="space-y-2">
                                <Label htmlFor="setup-code">Enter the 6-digit code to confirm</Label>
                                <Input
                                    id="setup-code"
                                    autoComplete="one-time-code"
                                    inputMode="numeric"
                                    placeholder="123456"
                                    required
                                    value={code}
                                    onChange={(e) => setCode(e.target.value)}
                                    className="max-w-40"
                                />
                            </div>

                            <div className="flex gap-2">
                                <Button type="submit" disabled={enable.isPending}>
                                    {enable.isPending ? 'Confirming…' : 'Turn on'}
                                </Button>
                                <Button type="button" variant="ghost" onClick={() => setPending(null)}>
                                    Cancel
                                </Button>
                            </div>
                        </form>
                    )}

                    {/* Idle states. */}
                    {!pending && !enabled && (
                        <Button type="button" onClick={beginSetup} disabled={setup.isPending || status.isLoading}>
                            {setup.isPending ? 'Starting…' : 'Set up two-factor'}
                        </Button>
                    )}

                    {!pending && enabled && (
                        <form onSubmit={confirmDisable} className="space-y-3">
                            <div className="space-y-2">
                                <Label htmlFor="disable-code">Turn off two-factor</Label>
                                <p className="text-muted-foreground text-sm">
                                    Enter a current code (or a recovery code) to confirm it&apos;s you.
                                </p>
                                <Input
                                    id="disable-code"
                                    autoComplete="one-time-code"
                                    inputMode="numeric"
                                    placeholder="123456"
                                    required
                                    value={disableCode}
                                    onChange={(e) => setDisableCode(e.target.value)}
                                    className="max-w-40"
                                />
                            </div>
                            <Button type="submit" variant="destructive" disabled={disable.isPending}>
                                {disable.isPending ? 'Turning off…' : 'Turn off'}
                            </Button>
                        </form>
                    )}
                </CardContent>
            </Card>
        </div>
    );
}
