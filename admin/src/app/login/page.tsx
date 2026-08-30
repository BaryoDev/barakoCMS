'use client';

import { useState, useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import { useAuth, useLogin, useVerifyDeviceCode, useVerifyMfa } from '@/hooks/use-auth';
import { apiErrorMessage } from '@/lib/api';
import { BrandMark } from '@/components/brand';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { IconEye, IconEyeSlash } from '@/components/icons';

export default function LoginPage() {
  const router = useRouter();
  const { isAuthenticated, isLoading } = useAuth();
  const login = useLogin();
  const verifyMfa = useVerifyMfa();
  const verifyDevice = useVerifyDeviceCode();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  // Set when the password was accepted but a second factor is required. Holds the short-lived
  // challenge, so the password never has to be kept around or re-sent.
  const [challengeToken, setChallengeToken] = useState<string | null>(null);
  // Set when the server emailed a device approval code. Holds the address it sent to, because
  // /api/auth/otp/verify identifies the account by email rather than by the username typed above.
  const [deviceEmail, setDeviceEmail] = useState<string | null>(null);
  const [code, setCode] = useState('');

  useEffect(() => {
    if (!isLoading && isAuthenticated) router.replace('/');
  }, [isLoading, isAuthenticated, router]);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    login.mutate(
      { username, password },
      {
        onSuccess: (data) => {
          if (data.requiresMfa && data.mfaChallengeToken) {
            setChallengeToken(data.mfaChallengeToken);
            setPassword('');
            return;
          }
          if (data.requiresDeviceApproval) {
            // The server sends the address it mailed. Without it there is nothing to verify
            // against, so fall back to the sign-in step rather than showing a form that cannot
            // succeed.
            if (!data.email) {
              toast.error('This device needs approval, but the server did not say where the code was sent.');
              return;
            }
            setDeviceEmail(data.email);
            setCode('');
            setPassword('');
            toast.info(data.message ?? 'Check your email for a device approval code.');
            return;
          }
          router.push('/');
        },
        onError: (error) =>
          toast.error(apiErrorMessage(error, 'Wrong username or password. After 5 failed tries the account locks for 15 minutes.')),
      }
    );
  };

  const handleVerify = (e: React.FormEvent) => {
    e.preventDefault();
    if (!challengeToken) return;
    verifyMfa.mutate(
      { challengeToken, code: code.trim() },
      {
        onSuccess: () => router.push('/'),
        onError: (error) => {
          setCode('');
          toast.error(
            apiErrorMessage(error, 'That code was not accepted. Codes rotate every 30 seconds — try the current one.')
          );
        },
      }
    );
  };

  const handleDeviceVerify = (e: React.FormEvent) => {
    e.preventDefault();
    if (!deviceEmail) return;
    verifyDevice.mutate(
      { email: deviceEmail, code: code.trim() },
      {
        onSuccess: (data) => {
          // A correct email code on an MFA account owes a second factor and issues no tokens, so
          // this hands off to the existing challenge step rather than treating it as signed in.
          if (data.requiresMfa && data.mfaChallengeToken) {
            setChallengeToken(data.mfaChallengeToken);
            setDeviceEmail(null);
            setCode('');
            return;
          }
          router.push('/');
        },
        onError: (error) => {
          setCode('');
          toast.error(apiErrorMessage(error, 'That code was not accepted. Check the most recent email.'));
        },
      }
    );
  };

  const startOver = () => {
    setChallengeToken(null);
    setDeviceEmail(null);
    setCode('');
    setPassword('');
  };

  return (
    <div className="flex min-h-svh items-center justify-center p-6">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex flex-col items-center gap-3 text-center">
          <BrandMark className="size-10" />
          <div>
            <h1 className="font-display text-2xl font-semibold tracking-tight">BarakoCMS</h1>
            <p className="text-muted-foreground mt-1 text-sm">
              {challengeToken
                ? 'Enter your authentication code'
                : deviceEmail
                  ? 'Approve this device'
                  : 'Sign in to manage your content'}
            </p>
          </div>
        </div>

        {deviceEmail ? (
          <form onSubmit={handleDeviceVerify} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="device-code">Device approval code</Label>
              <Input
                id="device-code"
                autoComplete="one-time-code"
                inputMode="numeric"
                // eslint-disable-next-line jsx-a11y/no-autofocus -- this field appears after the password step, so focus follows the user's own action rather than seizing it on load.
                autoFocus
                required
                placeholder="123456"
                value={code}
                onChange={(e) => setCode(e.target.value)}
              />
              <p className="text-muted-foreground text-xs">
                Sent to {deviceEmail}. Approving here trusts this browser for future sign-ins.
              </p>
            </div>
            <Button type="submit" className="w-full" disabled={verifyDevice.isPending}>
              {verifyDevice.isPending ? 'Verifying…' : 'Approve device'}
            </Button>
            <Button type="button" variant="ghost" className="w-full" onClick={startOver}>
              Back to sign in
            </Button>
          </form>
        ) : challengeToken ? (
          <form onSubmit={handleVerify} className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="code">Authentication code</Label>
              <Input
                id="code"
                // one-time-code lets password managers and iOS autofill offer the TOTP directly.
                autoComplete="one-time-code"
                inputMode="numeric"
                // eslint-disable-next-line jsx-a11y/no-autofocus -- this field appears after the password step, so focus is following the user's own action rather than seizing it on load.
                autoFocus
                required
                placeholder="123456"
                value={code}
                onChange={(e) => setCode(e.target.value)}
              />
              <p className="text-muted-foreground text-xs">
                From your authenticator app. You can also use one of your recovery codes.
              </p>
            </div>
            <Button type="submit" className="w-full" disabled={verifyMfa.isPending}>
              {verifyMfa.isPending ? 'Verifying…' : 'Verify'}
            </Button>
            <Button type="button" variant="ghost" className="w-full" onClick={startOver}>
              Back to sign in
            </Button>
          </form>
        ) : (
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="username">Username</Label>
            <Input
              id="username"
              autoComplete="username"
              // eslint-disable-next-line jsx-a11y/no-autofocus -- a sign-in page has one purpose and this is its first field, so the disorientation the rule guards against does not apply.
              autoFocus
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <div className="relative">
              <Input
                id="password"
                type={showPassword ? 'text' : 'password'}
                autoComplete="current-password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="pr-10"
              />
              <button
                type="button"
                onClick={() => setShowPassword((v) => !v)}
                className="text-muted-foreground hover:text-foreground absolute inset-y-0 right-0 flex items-center px-3"
                aria-label={showPassword ? 'Hide password' : 'Show password'}
              >
                {showPassword ? <IconEyeSlash className="size-4" /> : <IconEye className="size-4" />}
              </button>
            </div>
          </div>
          <Button type="submit" className="w-full" disabled={login.isPending}>
            {login.isPending ? 'Signing in…' : 'Sign in'}
          </Button>
        </form>
        )}
      </div>
    </div>
  );
}
