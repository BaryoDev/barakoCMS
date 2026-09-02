'use client';

import { useState, useEffect, useSyncExternalStore } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import {
  useAuth,
  useLogin,
  useRequestSignInCode,
  useVerifyDeviceCode,
  useVerifyMfa,
} from '@/hooks/use-auth';
import { useAuthProviders, externalSignInUrl, type AuthProviders } from '@/hooks/use-auth-providers';
import { apiErrorMessage, getApiUrl } from '@/lib/api';
import { BrandBean } from '@/components/brand';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { IconArrowRight, IconEnvelope, IconExternalLink, IconEye, IconEyeSlash } from '@/components/icons';

const emptySubscribe = () => () => {};

function apiHostSnapshot(): string | null {
  try {
    return new URL(getApiUrl()).host;
  } catch {
    return null;
  }
}

/** The 42px form control the sign-in card uses, on the page tint rather than the card's white. */
const FIELD = 'h-[42px] bg-background text-sm';

const PROVIDER_LABELS: Record<keyof AuthProviders, string> = {
  github: 'GitHub',
  google: 'Google',
  linkedin: 'LinkedIn',
  facebook: 'Facebook',
};

// Ordered, because the object key order of a JSON response is not a design decision.
const PROVIDER_ORDER: (keyof AuthProviders)[] = ['github', 'google', 'linkedin', 'facebook'];

/**
 * Which step the sign-in is on.
 *
 * `device` and `code` both finish at POST /api/auth/otp/verify and differ only in how they were
 * reached: `device` is the server refusing an unrecognised browser after a correct password, `code`
 * is the user choosing email instead of a password. They keep separate copy and separate field
 * labels because they are separate events to the person reading them.
 */
type Step = 'password' | 'mfa' | 'device' | 'email' | 'code';

export default function LoginPage() {
  const router = useRouter();
  const { isAuthenticated, isLoading } = useAuth();
  const login = useLogin();
  const verifyMfa = useVerifyMfa();
  const verifyDevice = useVerifyDeviceCode();
  const requestCode = useRequestSignInCode();
  const { data: providers } = useAuthProviders();

  const [step, setStep] = useState<Step>('password');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  // Set when the password was accepted but a second factor is required. Holds the short-lived
  // challenge, so the password never has to be kept around or re-sent.
  const [challengeToken, setChallengeToken] = useState<string | null>(null);
  // The address /api/auth/otp/verify identifies the account by. On the device path the server says
  // where it mailed; on the email path the user typed it.
  const [email, setEmail] = useState('');
  const [code, setCode] = useState('');

  // The API host, so an operator can see which instance this page is signing into. Read on the
  // client only: getApiUrl() reads window._env_, which the server does not have, and a value that
  // differs between the two renders is a hydration mismatch. The server snapshot is null, matching
  // what layout.tsx already does for the session token.
  const apiHost = useSyncExternalStore(emptySubscribe, apiHostSnapshot, () => null);

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
            setStep('mfa');
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
            setEmail(data.email);
            setCode('');
            setPassword('');
            setStep('device');
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
            apiErrorMessage(error, 'That code was not accepted. Codes rotate every 30 seconds, try the current one.')
          );
        },
      }
    );
  };

  const handleRequestCode = (e: React.FormEvent) => {
    e.preventDefault();
    requestCode.mutate(
      { email: email.trim() },
      {
        onSuccess: (data) => {
          setCode('');
          setStep('code');
          // The endpoint answers identically whether or not the address is registered, so this
          // repeats what it said rather than claiming an email was sent.
          toast.info(data.message ?? 'If that email is registered, a sign-in code has been sent.');
        },
        onError: (error) => toast.error(apiErrorMessage(error, 'Could not request a sign-in code.')),
      }
    );
  };

  const handleDeviceVerify = (e: React.FormEvent) => {
    e.preventDefault();
    if (!email) return;
    verifyDevice.mutate(
      { email, code: code.trim() },
      {
        onSuccess: (data) => {
          // A correct email code on an MFA account owes a second factor and issues no tokens, so
          // this hands off to the existing challenge step rather than treating it as signed in.
          if (data.requiresMfa && data.mfaChallengeToken) {
            setChallengeToken(data.mfaChallengeToken);
            setCode('');
            setStep('mfa');
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
    setStep('password');
    setChallengeToken(null);
    setEmail('');
    setCode('');
    setPassword('');
  };

  const subtitle =
    step === 'mfa'
      ? 'Enter your authentication code'
      : step === 'device'
        ? 'Approve this device'
        : step === 'email'
          ? 'Sign in with an emailed code'
          : step === 'code'
            ? 'Enter the code we emailed'
            : null;

  const enabledProviders = PROVIDER_ORDER.filter((p) => providers?.[p]);

  return (
    <div className="bg-background relative flex min-h-svh items-center justify-center overflow-hidden p-8">
      {/* The mark, oversized and bleeding off the corner. Decorative: the same bean carries the
          accessible name 44px higher up, and naming it twice is noise to a screen reader. */}
      <BrandBean
        decorative
        className="pointer-events-none absolute -bottom-[90px] -left-[90px] size-[300px] opacity-[0.07]"
      />

      <div className="relative w-full max-w-[340px]">
        <div className="flex flex-col items-center gap-3.5 text-center">
          <BrandBean className="size-11" />
          <div>
            <h1 className="font-display text-2xl font-semibold tracking-[-0.03em]">
              Sign in to barako<span className="text-primary">Brew</span>
            </h1>
            {subtitle ? (
              <p className="text-muted-foreground mt-1.5 text-[13.5px]">{subtitle}</p>
            ) : (
              <p className="text-muted-foreground mt-1.5 font-mono text-xs tabular-nums">
                {apiHost ?? ' '}
              </p>
            )}
          </div>
        </div>

        <div className="bg-card mt-7 flex flex-col gap-4 rounded-2xl border p-6 shadow-[0_4px_16px_-8px_rgba(16,18,35,0.12)]">
          {step === 'mfa' ? (
            <form onSubmit={handleVerify} className="flex flex-col gap-4">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="code" className="text-secondary-foreground text-[12.5px] font-bold">
                  Authentication code
                </Label>
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
                  className={`${FIELD} font-mono tabular-nums`}
                />
                <p className="text-[var(--faint)] text-xs">
                  From your authenticator app. You can also use one of your recovery codes.
                </p>
              </div>
              <SubmitButton pending={verifyMfa.isPending} pendingLabel="Verifying…">
                Verify
              </SubmitButton>
              <BackButton onClick={startOver} />
            </form>
          ) : step === 'device' || step === 'code' ? (
            <form onSubmit={handleDeviceVerify} className="flex flex-col gap-4">
              <div className="flex flex-col gap-1.5">
                <Label
                  htmlFor="device-code"
                  className="text-secondary-foreground text-[12.5px] font-bold"
                >
                  {step === 'device' ? 'Device approval code' : 'Sign-in code'}
                </Label>
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
                  className={`${FIELD} font-mono tabular-nums`}
                />
                <p className="text-[var(--faint)] text-xs">
                  {step === 'device' ? (
                    <>
                      Sent to <span className="font-mono">{email}</span>. Approving here trusts this
                      browser for future sign-ins.
                    </>
                  ) : (
                    <>
                      Sent to <span className="font-mono">{email}</span> if that address is
                      registered.
                    </>
                  )}
                </p>
              </div>
              <SubmitButton pending={verifyDevice.isPending} pendingLabel="Verifying…">
                {step === 'device' ? 'Approve device' : 'Continue'}
              </SubmitButton>
              <BackButton onClick={startOver} />
            </form>
          ) : step === 'email' ? (
            <form onSubmit={handleRequestCode} className="flex flex-col gap-4">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="email" className="text-secondary-foreground text-[12.5px] font-bold">
                  Email address
                </Label>
                <Input
                  id="email"
                  type="email"
                  autoComplete="email"
                  // eslint-disable-next-line jsx-a11y/no-autofocus -- this field appears after the user chose the emailed-code button, so focus follows their own action.
                  autoFocus
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className={FIELD}
                />
                <p className="text-[var(--faint)] text-xs">
                  The address on your account, not your username. We answer the same way whether or
                  not it is registered.
                </p>
              </div>
              <SubmitButton pending={requestCode.isPending} pendingLabel="Sending…">
                Send the code
              </SubmitButton>
              <BackButton onClick={startOver} />
            </form>
          ) : (
            <>
              <form onSubmit={handleSubmit} className="flex flex-col gap-4">
                <div className="flex flex-col gap-1.5">
                  <Label
                    htmlFor="username"
                    className="text-secondary-foreground text-[12.5px] font-bold"
                  >
                    Username
                  </Label>
                  <Input
                    id="username"
                    autoComplete="username"
                    // eslint-disable-next-line jsx-a11y/no-autofocus -- a sign-in page has one purpose and this is its first field, so the disorientation the rule guards against does not apply.
                    autoFocus
                    required
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    className={FIELD}
                  />
                </div>
                <div className="flex flex-col gap-1.5">
                  {/* The design puts a "Forgot?" link here. There is no password reset in this
                      product: Features/Auth/ holds Login, Logout, Mfa, Otp, Refresh and Register
                      and nothing else, so the link would go nowhere. The emailed code below is the
                      route back in that does exist. See #416. */}
                  <Label
                    htmlFor="password"
                    className="text-secondary-foreground text-[12.5px] font-bold"
                  >
                    Password
                  </Label>
                  <div className="relative">
                    <Input
                      id="password"
                      type={showPassword ? 'text' : 'password'}
                      autoComplete="current-password"
                      required
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      className={`${FIELD} pr-11`}
                    />
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon-sm"
                      onClick={() => setShowPassword((v) => !v)}
                      className="text-muted-foreground absolute top-[5px] right-[5px]"
                      aria-label={showPassword ? 'Hide password' : 'Show password'}
                    >
                      {showPassword ? (
                        <IconEyeSlash className="size-3.5" />
                      ) : (
                        <IconEye className="size-3.5" />
                      )}
                    </Button>
                  </div>
                </div>
                <SubmitButton pending={login.isPending} pendingLabel="Signing in…">
                  Sign in
                  <IconArrowRight className="size-3.5" />
                </SubmitButton>
              </form>

              <div className="flex items-center gap-3">
                <span className="bg-border h-px flex-1" />
                <span className="text-[var(--faint)] text-[11.5px] font-bold">OR</span>
                <span className="bg-border h-px flex-1" />
              </div>

              <div className="flex flex-col gap-2">
                <AlternateButton onClick={() => setStep('email')} icon={IconEnvelope}>
                  Email me a sign-in code
                </AlternateButton>
                {/* Rendered from GET /api/auth/providers rather than unconditionally. The
                    ExternalAuth module is optional and a provider with no client id is off even
                    when it is installed, so a hardcoded button is dead on the default deployment. */}
                {enabledProviders.map((provider) => (
                  <AlternateButton
                    key={provider}
                    icon={IconExternalLink}
                    onClick={() => {
                      // A full navigation, not fetch: this is an OAuth redirect that has to leave
                      // the SPA and come back through the callback.
                      window.location.href = externalSignInUrl(provider);
                    }}
                  >
                    Continue with {PROVIDER_LABELS[provider]}
                  </AlternateButton>
                ))}
              </div>
            </>
          )}
        </div>

        <p className="mt-4.5 text-center text-xs leading-relaxed text-[var(--faint)]">
          Five failed attempts locks the account for 15 minutes. A new device asks for an emailed
          code.
        </p>
      </div>
    </div>
  );
}

function SubmitButton({
  pending,
  pendingLabel,
  children,
}: {
  pending: boolean;
  pendingLabel: string;
  children: React.ReactNode;
}) {
  return (
    <Button
      type="submit"
      disabled={pending}
      className="h-11 w-full text-[14.5px] font-bold shadow-[var(--shadow-accent)]"
    >
      {pending ? pendingLabel : children}
    </Button>
  );
}

function BackButton({ onClick }: { onClick: () => void }) {
  return (
    <Button type="button" variant="ghost" className="h-10 w-full" onClick={onClick}>
      Back to sign in
    </Button>
  );
}

function AlternateButton({
  icon: Icon,
  onClick,
  children,
}: {
  icon: (props: React.SVGProps<SVGSVGElement>) => React.ReactElement;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <Button
      type="button"
      variant="outline"
      onClick={onClick}
      className="text-secondary-foreground hover:border-primary hover:text-accent-foreground h-10 w-full text-[13.5px] font-semibold"
    >
      <Icon className="size-3.5" />
      {children}
    </Button>
  );
}
