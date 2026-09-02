'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import { formatDistanceToNowStrict } from 'date-fns';
import { useDevices, useRevokeDevice, type Device } from '@/hooks/use-devices';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { StatusBadge } from '@/components/patterns/status-badge';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { IconShield, IconTrash } from '@/components/icons';

const HEAD =
  'h-auto bg-background py-3 text-[10.5px] font-extrabold tracking-[0.12em] uppercase text-[var(--faint)]';

const META = 'text-muted-foreground font-mono text-[11.5px] tabular-nums';

/** Server-sent statuses only. An unknown one renders as itself rather than as a guess. */
function statusTone(status: string): 'success' | 'warning' | 'muted' {
  if (status === 'Trusted') return 'success';
  if (status === 'Pending') return 'warning';
  return 'muted';
}

export default function DevicesPage() {
  const { data, isLoading, isError, refetch } = useDevices();
  const revoke = useRevokeDevice();
  const [confirming, setConfirming] = useState<Device | null>(null);

  const devices = data?.items ?? [];

  const onRevoke = async () => {
    if (!confirming) return;

    // Captured before the mutation, because the dialog closes and `confirming` is null by the time
    // the message is written. Whether it was this device changes what happens next, not just wording.
    const wasCurrent = confirming.current;

    try {
      await revoke.mutateAsync(confirming.id);
      setConfirming(null);
      toast.success(
        wasCurrent
          ? 'This device was revoked. You will be signed out of it.'
          : 'Device revoked.'
      );
    } catch (error) {
      toast.error(apiErrorMessage(error));
    }
  };

  return (
    <>
      <PageHeader
        title="Devices"
        description="Browsers and apps signed in to your account. Revoking one ends its access."
      />

      {isLoading ? (
        <TableSkeleton />
      ) : isError ? (
        <ErrorState entity="devices" onRetry={() => refetch()} />
      ) : devices.length === 0 ? (
        <EmptyState
          icon={IconShield}
          title="No devices recorded"
          description="A device appears here the first time it signs in to your account."
        />
      ) : (
        <div className="bg-card overflow-hidden rounded-xl border shadow-[var(--shadow-card)]">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-background">
                <TableHead className={`${HEAD} pl-6`}>Device</TableHead>
                <TableHead className={HEAD}>Status</TableHead>
                <TableHead className={`${HEAD} hidden sm:table-cell`}>Last seen from</TableHead>
                <TableHead className={`${HEAD} hidden text-right md:table-cell`}>Last used</TableHead>
                <TableHead className={`${HEAD} pr-6 text-right`}>
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {devices.map((device) => (
                <TableRow key={device.id} className="hover:bg-background">
                  <TableCell className="py-3.5 pl-6">
                    <div className="flex items-center gap-2">
                      <span className="text-[13.5px] font-bold">{device.description}</span>
                      {device.current && (
                        <StatusBadge tone="accent" dot={false}>
                          This device
                        </StatusBadge>
                      )}
                    </div>
                  </TableCell>
                  <TableCell className="py-3.5">
                    <StatusBadge tone={statusTone(device.status)} dot={false}>
                      {device.status}
                    </StatusBadge>
                  </TableCell>
                  <TableCell className={`${META} hidden py-3.5 sm:table-cell`}>
                    {device.lastSeenIp || 'Not recorded'}
                  </TableCell>
                  <TableCell className={`${META} hidden py-3.5 text-right md:table-cell`}>
                    {formatDistanceToNowStrict(new Date(device.lastUsedAt), { addSuffix: true })}
                  </TableCell>
                  <TableCell className="py-3.5 pr-6 text-right">
                    {/* A revoked device has nothing left to revoke, so the control is gone rather
                        than disabled: a disabled button invites a click and explains nothing. */}
                    {device.status === 'Revoked' ? (
                      <span className={META}>Revoked</span>
                    ) : (
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => setConfirming(device)}
                        aria-label={`Revoke ${device.description}`}
                      >
                        <IconTrash />
                        Revoke
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <Dialog open={confirming !== null} onOpenChange={(open) => !open && setConfirming(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Revoke this device?</DialogTitle>
            <DialogDescription>
              {confirming?.current
                ? 'This is the device you are using now. Revoking it signs you out here, and you will need to sign in again.'
                : `${confirming?.description ?? 'This device'} will lose access. Signing in from it again creates a new device.`}
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(null)}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={onRevoke} disabled={revoke.isPending}>
              {revoke.isPending ? 'Revoking...' : 'Revoke'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
