'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
  useEmailSettings,
  useSendTestEmail,
  useUpdateEmailSettings,
  type EmailSettingSource,
} from '@/hooks/use-settings';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';

function sourceLabel(source: EmailSettingSource) {
  if (source === 'Stored') return 'Set here';
  if (source === 'Configuration') return 'From the deployment';
  return 'Not set';
}

export default function EmailSettingsPage() {
  const { data, isLoading } = useEmailSettings();
  const update = useUpdateEmailSettings();
  const sendTest = useSendTestEmail();

  // Undefined until somebody types, so an untouched field is sent as null and left alone. The API
  // cannot return the current key, so there is nothing to prefill and nothing to send back.
  const [apiKey, setApiKey] = useState<string | undefined>(undefined);
  const [fromAddress, setFromAddress] = useState<string | undefined>(undefined);

  const save = () => {
    update.mutate(
      { apiKey: apiKey ?? null, fromAddress: fromAddress ?? null },
      {
        onSuccess: () => {
          setApiKey(undefined);
          setFromAddress(undefined);
          toast.success('Email settings saved');
        },
        onError: (error) => toast.error(apiErrorMessage(error, 'The settings could not be saved.')),
      },
    );
  };

  const test = () => {
    sendTest.mutate(undefined, {
      onSuccess: (result) => toast.success(result.message),
      onError: (error) => toast.error(apiErrorMessage(error, 'The test send failed.')),
    });
  };

  if (isLoading || !data) return <TableSkeleton />;

  return (
    <>
      <PageHeader
        title="Email"
        description="Where this instance sends email from. Saved here, encrypted, and used without a restart."
      />

      <div className="max-w-2xl space-y-4">
        {!data.providerRegistered && (
          <Card>
            <CardContent className="pt-6 text-sm">
              No email provider is registered, so nothing is delivered whatever is set here. Add a
              provider module, for instance <code>BarakoCMS.Email.Resend</code>, and restart.
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader>
            <CardTitle className="text-sm font-medium">Provider credentials</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <Label htmlFor="api-key">API key</Label>
                <Badge variant="secondary">{sourceLabel(data.apiKeySource)}</Badge>
              </div>
              <Input
                id="api-key"
                type="password"
                autoComplete="off"
                value={apiKey ?? ''}
                placeholder={data.apiKeySet ? 'Set. Type to replace it.' : 'Not set'}
                onChange={(e) => setApiKey(e.target.value)}
              />
              <p className="text-muted-foreground text-xs">
                Stored encrypted and never shown again. Leave it alone to keep the current one, or
                clear the box and save to fall back to whatever the deployment configures.
              </p>
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <Label htmlFor="from-address">From address</Label>
                <Badge variant="secondary">{sourceLabel(data.fromAddressSource)}</Badge>
              </div>
              <Input
                id="from-address"
                value={fromAddress ?? data.fromAddress}
                placeholder="BarakoCMS &lt;billing@example.com&gt;"
                onChange={(e) => setFromAddress(e.target.value)}
              />
            </div>

            {data.updatedAt && (
              <p className="text-muted-foreground text-xs">
                Last changed {new Date(data.updatedAt).toLocaleString()}
                {data.updatedBy ? ` by ${data.updatedBy}` : ''}.
              </p>
            )}

            <div className="flex gap-2">
              <Button onClick={save} disabled={update.isPending}>
                {update.isPending ? 'Saving…' : 'Save'}
              </Button>
              <Button variant="outline" onClick={test} disabled={sendTest.isPending}>
                {sendTest.isPending ? 'Sending…' : 'Send a test to myself'}
              </Button>
            </div>
            <p className="text-muted-foreground text-xs">
              The test goes to your own address and nowhere else.
            </p>
          </CardContent>
        </Card>
      </div>
    </>
  );
}
