'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { useSettings, useUpdateSetting, type SystemSetting } from '@/hooks/use-settings';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Switch } from '@/components/ui/switch';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { IconSettings } from '@/components/icons';

function isBooleanValue(value: string) {
  return value === 'true' || value === 'false';
}

export default function SettingsPage() {
  const { data: settings, isLoading } = useSettings();

  const categories = settings
    ? [...new Set(settings.map((s) => s.category))].sort()
    : [];

  return (
    <>
      <PageHeader
        title="Settings"
        description="Runtime configuration. Changes take effect without a redeploy."
      />

      {isLoading ? (
        <TableSkeleton />
      ) : !settings?.length ? (
        <EmptyState
          icon={IconSettings}
          title="No settings recorded"
          description="Settings appear here once the backend registers them."
        />
      ) : (
        <div className="max-w-2xl space-y-4">
          {categories.map((category) => (
            <Card key={category}>
              <CardHeader>
                <CardTitle className="text-sm font-medium">{category}</CardTitle>
              </CardHeader>
              <CardContent className="divide-y">
                {settings
                  .filter((s) => s.category === category)
                  .map((setting) => (
                    <SettingRow key={setting.key} setting={setting} />
                  ))}
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </>
  );
}

function SettingRow({ setting }: { setting: SystemSetting }) {
  const updateSetting = useUpdateSetting();
  const [draft, setDraft] = useState(setting.value);

  const save = (value: string) => {
    updateSetting.mutate(
      { key: setting.key, value },
      {
        onSuccess: () => toast.success(`${setting.key} updated`),
        onError: (error) => toast.error(apiErrorMessage(error, 'The setting could not be updated.')),
      }
    );
  };

  return (
    <div className="flex items-center justify-between gap-4 py-3 first:pt-0 last:pb-0">
      <div className="min-w-0">
        <p className="font-mono text-sm">{setting.key}</p>
        {setting.description && (
          <p className="text-muted-foreground mt-0.5 text-xs">{setting.description}</p>
        )}
      </div>
      {isBooleanValue(setting.value) ? (
        <Switch
          checked={setting.value === 'true'}
          disabled={updateSetting.isPending}
          onCheckedChange={(checked) => save(String(checked))}
          aria-label={setting.key}
        />
      ) : (
        <div className="flex shrink-0 items-center gap-2">
          <Input
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            className="w-40 font-mono text-xs"
            aria-label={setting.key}
          />
          {draft !== setting.value && (
            <Button size="sm" variant="outline" disabled={updateSetting.isPending} onClick={() => save(draft)}>
              Save
            </Button>
          )}
        </div>
      )}
    </div>
  );
}
