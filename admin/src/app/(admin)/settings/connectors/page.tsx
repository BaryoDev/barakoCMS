'use client';

import { useState } from 'react';
import { toast } from 'sonner';
import {
    CONNECTOR_AUTH_MODES,
    authModeFor,
    configGap,
    probeOutcome,
    slugify,
    toSecretsPayload,
    useConnectors,
    useCreateConnector,
    useDeleteConnector,
    useTestConnector,
    useUpdateConnector,
    type Connector,
    type SaveConnectorInput,
} from '@/hooks/use-connectors';
import { apiErrorMessage } from '@/lib/api';
import { PageHeader } from '@/components/patterns/page-header';
import { EmptyState } from '@/components/patterns/empty-state';
import { ErrorState } from '@/components/patterns/error-state';
import { TableSkeleton } from '@/components/patterns/table-skeleton';
import { ConfirmDialog } from '@/components/patterns/confirm-dialog';
import { StatusBadge, type Tone } from '@/components/patterns/status-badge';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
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
import { IconBolt, IconPen, IconPlus, IconTrash, IconWarning, IconWebhook } from '@/components/icons';

const SELECT = 'h-9 w-full rounded-md border bg-transparent px-3 text-sm';

const PROBE_LABELS: Record<ReturnType<typeof probeOutcome>, { label: string; tone: Tone }> = {
    succeeded: { label: 'Reachable', tone: 'success' },
    failed: { label: 'Failing', tone: 'destructive' },
    untested: { label: 'Never tested', tone: 'muted' },
};

function formatWhen(value: string | null) {
    if (!value) return null;
    return new Date(value).toLocaleString(undefined, {
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
    });
}

function ProbeCell({ connector }: { connector: Connector }) {
    const outcome = probeOutcome(connector);
    const { label, tone } = PROBE_LABELS[outcome];
    const when = formatWhen(connector.lastTestedAt);

    return (
        <div className="space-y-1">
            <StatusBadge tone={tone}>{label}</StatusBadge>
            {connector.lastTestResult && (
                <p className="text-muted-foreground font-mono text-[11px]">{connector.lastTestResult}</p>
            )}
            {when && <p className="text-muted-foreground text-[11px]">{when}</p>}
        </div>
    );
}

interface ConnectorDialogProps {
    /** Null for a create. */
    connector: Connector | null;
    open: boolean;
    onOpenChange: (open: boolean) => void;
}

function ConnectorDialog({ connector, open, onOpenChange }: ConnectorDialogProps) {
    const create = useCreateConnector();
    const update = useUpdateConnector();
    const editing = connector !== null;

    const [name, setName] = useState(connector?.name ?? '');
    const [slug, setSlug] = useState(connector?.slug ?? '');
    const [slugEdited, setSlugEdited] = useState(editing);
    const [baseUrl, setBaseUrl] = useState(connector?.baseUrl ?? '');
    const [auth, setAuth] = useState(connector?.auth ?? 'None');
    const [probePath, setProbePath] = useState(connector?.probePath ?? '/');
    const [enabled, setEnabled] = useState(connector?.enabled ?? true);
    const [settings, setSettings] = useState<Record<string, string>>({ ...(connector?.settings ?? {}) });

    // The credential the operator typed this session, and the stored ones they asked to delete.
    // There is no third state: nothing holds a value read back from the server, because no endpoint
    // returns one.
    const [secretValue, setSecretValue] = useState('');
    const [clearKeys, setClearKeys] = useState<string[]>([]);

    const mode = authModeFor(auth);
    const storedKeys = connector?.secretKeys ?? [];
    const secretKey = mode?.secretKey ?? null;
    const secretStored = secretKey !== null && storedKeys.includes(secretKey);
    const pending = create.isPending || update.isPending;
    const canSave = name.trim().length > 0 && slug.length > 0 && baseUrl.trim().length > 0 && !pending;

    function onNameChange(value: string) {
        setName(value);
        // The slug cannot be changed after a create, so it is derived until the operator takes it
        // over, and never touched again once they have.
        if (!slugEdited) setSlug(slugify(value));
    }

    function onAuthChange(value: string) {
        setAuth(value);
        // The typed box belonged to the old mode's credential name. Carrying it over would store,
        // say, a Password under the name Token.
        setSecretValue('');
    }

    function toggleClear(key: string, checked: boolean) {
        setClearKeys((current) =>
            checked ? [...new Set([...current, key])] : current.filter((k) => k !== key),
        );
    }

    async function submit(event: React.FormEvent) {
        event.preventDefault();
        if (!canSave) return;

        const payload: SaveConnectorInput = {
            name: name.trim(),
            slug,
            baseUrl: baseUrl.trim(),
            auth,
            // Spread from what was loaded, so a setting this screen does not know about survives a
            // save: the PUT replaces the whole dictionary rather than merging it.
            settings,
            enabled,
            probePath: probePath.trim() || '/',
            secrets: secretKey
                ? toSecretsPayload({ [secretKey]: secretValue }, clearKeys)
                : toSecretsPayload({}, clearKeys),
        };

        try {
            if (editing) {
                await update.mutateAsync(payload);
                toast.success(`Saved "${payload.name}"`);
            } else {
                await create.mutateAsync(payload);
                toast.success(`Added "${payload.name}"`);
            }
            onOpenChange(false);
        } catch (error) {
            toast.error(apiErrorMessage(error, 'Could not save the connector.'));
        }
    }

    return (
        <Dialog open={open} onOpenChange={onOpenChange}>
            <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-lg">
                <form onSubmit={submit}>
                    <DialogHeader>
                        <DialogTitle>{editing ? `Edit ${connector.name}` : 'New connector'}</DialogTitle>
                        <DialogDescription>
                            Where a third party lives and how to authenticate to it. A credential is stored
                            encrypted and is never returned to this screen, by any endpoint.
                        </DialogDescription>
                    </DialogHeader>

                    <div className="space-y-4 py-4">
                        <div className="space-y-1.5">
                            <Label htmlFor="connector-name">Name</Label>
                            <Input
                                id="connector-name"
                                value={name}
                                onChange={(e) => onNameChange(e.target.value)}
                                placeholder="Company Jira"
                            />
                        </div>

                        <div className="space-y-1.5">
                            <Label htmlFor="connector-slug">Slug</Label>
                            <Input
                                id="connector-slug"
                                value={slug}
                                disabled={editing}
                                className="font-mono text-xs"
                                onChange={(e) => {
                                    setSlugEdited(true);
                                    setSlug(slugify(e.target.value));
                                }}
                                placeholder="company-jira"
                            />
                            <p className="text-muted-foreground text-xs">
                                {editing
                                    ? 'Fixed after creation. A request definition references a connector by slug, so renaming it would break those without saying so.'
                                    : 'Lowercase letters, digits and hyphens. This is what a request definition will reference, and it cannot be changed later.'}
                            </p>
                        </div>

                        <div className="space-y-1.5">
                            <Label htmlFor="connector-base-url">Base URL</Label>
                            <Input
                                id="connector-base-url"
                                value={baseUrl}
                                onChange={(e) => setBaseUrl(e.target.value)}
                                placeholder="https://jira.example.com"
                            />
                        </div>

                        <div className="space-y-1.5">
                            <Label htmlFor="connector-auth">Authentication</Label>
                            <select
                                id="connector-auth"
                                className={SELECT}
                                value={auth}
                                onChange={(e) => onAuthChange(e.target.value)}
                            >
                                {CONNECTOR_AUTH_MODES.map((option) => (
                                    <option key={option.value} value={option.value}>
                                        {option.label}
                                    </option>
                                ))}
                                {/* A mode the server has and this build does not. Kept selectable so
                                    opening the form does not silently downgrade it to None. */}
                                {!mode && <option value={auth}>{auth}</option>}
                            </select>
                            {mode && <p className="text-muted-foreground text-xs">{mode.description}</p>}
                            {mode?.unsupported && (
                                <p className="text-warning flex gap-2 text-xs">
                                    <IconWarning aria-hidden className="mt-0.5 shrink-0 size-3.5" />
                                    <span>{mode.unsupported}</span>
                                </p>
                            )}
                        </div>

                        {mode?.settings.map((setting) => (
                            <div key={setting.key} className="space-y-1.5">
                                <Label htmlFor={`connector-setting-${setting.key}`}>{setting.label}</Label>
                                <Input
                                    id={`connector-setting-${setting.key}`}
                                    value={settings[setting.key] ?? ''}
                                    placeholder={setting.placeholder}
                                    onChange={(e) =>
                                        setSettings((current) => ({ ...current, [setting.key]: e.target.value }))
                                    }
                                />
                                <p className="text-muted-foreground text-xs">
                                    Not a secret. It is stored on the connector and returned by the API.
                                </p>
                            </div>
                        ))}

                        {secretKey && (
                            <div className="space-y-1.5">
                                <Label htmlFor="connector-secret">
                                    {secretStored ? `Replace the stored ${secretKey}` : secretKey}
                                </Label>
                                <Input
                                    id="connector-secret"
                                    type="password"
                                    autoComplete="off"
                                    value={secretValue}
                                    onChange={(e) => setSecretValue(e.target.value)}
                                    placeholder={secretStored ? 'Leave blank to keep it' : 'Paste the value'}
                                />
                                <p className="text-muted-foreground text-xs">
                                    {secretStored
                                        ? 'A stored credential is never sent back here, so this box starts blank. Blank means keep what is stored.'
                                        : 'Stored encrypted under Connectors:Key. Nothing reads it back out, so keep your own copy.'}
                                </p>
                            </div>
                        )}

                        {storedKeys.length > 0 && (
                            <div className="space-y-2 rounded-lg border p-3">
                                <p className="text-xs font-bold">Stored credentials</p>
                                {storedKeys.map((key) => (
                                    <div key={key} className="flex items-center gap-2.5">
                                        <Checkbox
                                            id={`connector-clear-${key}`}
                                            checked={clearKeys.includes(key)}
                                            onCheckedChange={(checked) => toggleClear(key, checked === true)}
                                        />
                                        <Label htmlFor={`connector-clear-${key}`} className="text-sm font-normal">
                                            Delete the stored {key}
                                            {secretKey !== key && (
                                                <span className="text-muted-foreground ml-1.5 text-xs">
                                                    not used by this auth mode
                                                </span>
                                            )}
                                        </Label>
                                    </div>
                                ))}
                            </div>
                        )}

                        <div className="space-y-1.5">
                            <Label htmlFor="connector-probe-path">Probe path</Label>
                            <Input
                                id="connector-probe-path"
                                value={probePath}
                                className="font-mono text-xs"
                                onChange={(e) => setProbePath(e.target.value)}
                                placeholder="/"
                            />
                            <p className="text-muted-foreground text-xs">
                                What the test button requests, relative to the base URL. Point it at something
                                that answers 200 for a working credential, since a test that reports 404 for a
                                healthy connector teaches everyone to ignore it.
                            </p>
                        </div>

                        <div className="flex items-center gap-2.5">
                            <Switch
                                id="connector-enabled"
                                checked={enabled}
                                onCheckedChange={(checked) => setEnabled(checked)}
                            />
                            <Label htmlFor="connector-enabled" className="font-normal">
                                Enabled
                            </Label>
                        </div>
                    </div>

                    <DialogFooter>
                        <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
                            Cancel
                        </Button>
                        <Button type="submit" disabled={!canSave}>
                            {pending ? 'Saving...' : editing ? 'Save changes' : 'Add connector'}
                        </Button>
                    </DialogFooter>
                </form>
            </DialogContent>
        </Dialog>
    );
}

export default function ConnectorsPage() {
    const { data: connectors, isLoading, isError, refetch } = useConnectors();
    const test = useTestConnector();
    const remove = useDeleteConnector();

    const [dialogOpen, setDialogOpen] = useState(false);
    const [editing, setEditing] = useState<Connector | null>(null);
    const [testing, setTesting] = useState<string | null>(null);

    function openCreate() {
        setEditing(null);
        setDialogOpen(true);
    }

    function openEdit(connector: Connector) {
        setEditing(connector);
        setDialogOpen(true);
    }

    async function onTest(connector: Connector) {
        setTesting(connector.slug);
        try {
            const result = await test.mutateAsync(connector.slug);
            if (result.succeeded) {
                toast.success(`${connector.name} answered ${result.statusCode} in ${result.elapsedMs} ms`);
            } else {
                toast.error(
                    result.error ?? `${connector.name} answered ${result.statusCode} in ${result.elapsedMs} ms`,
                );
            }
        } catch (error) {
            toast.error(apiErrorMessage(error, 'The test could not be run.'));
        } finally {
            setTesting(null);
        }
    }

    async function onDelete(connector: Connector) {
        try {
            await remove.mutateAsync(connector.slug);
            toast.success(`Deleted "${connector.name}"`);
        } catch (error) {
            toast.error(apiErrorMessage(error, 'Could not delete the connector.'));
        }
    }

    const newButton = (
        <Button size="sm" onClick={openCreate}>
            <IconPlus />
            New connector
        </Button>
    );

    return (
        <>
            <PageHeader
                title="Connectors"
                description="A third party this instance can call: where it lives, how to authenticate, and whether it answered."
                actions={newButton}
            />

            {isLoading ? (
                <TableSkeleton />
            ) : isError ? (
                <ErrorState entity="connectors" onRetry={() => refetch()} />
            ) : !connectors?.length ? (
                <EmptyState
                    icon={IconWebhook}
                    title="No connectors yet"
                    description="Add one to hold a third party's base URL and credentials, so a workflow can call it without anybody writing code."
                    action={newButton}
                />
            ) : (
                <div className="rounded-lg border">
                    <Table>
                        <TableHeader>
                            <TableRow>
                                <TableHead>Connector</TableHead>
                                <TableHead>Base URL</TableHead>
                                <TableHead>Auth</TableHead>
                                <TableHead>Credentials</TableHead>
                                <TableHead>Last probe</TableHead>
                                <TableHead className="w-28" />
                            </TableRow>
                        </TableHeader>
                        <TableBody>
                            {connectors.map((connector) => {
                                const gap = configGap(connector);
                                const mode = authModeFor(connector.auth);

                                return (
                                    <TableRow key={connector.id}>
                                        <TableCell className="align-top">
                                            <div className="font-medium">
                                                {connector.name}
                                                {!connector.enabled && (
                                                    <Badge variant="secondary" className="ml-2 text-[11px]">
                                                        Disabled
                                                    </Badge>
                                                )}
                                            </div>
                                            <div className="text-muted-foreground font-mono text-xs">
                                                {connector.slug}
                                            </div>
                                            {gap && (
                                                <p className="text-warning mt-1 flex max-w-xs gap-1.5 text-[11px]">
                                                    <IconWarning aria-hidden className="mt-0.5 size-3 shrink-0" />
                                                    <span>{gap}</span>
                                                </p>
                                            )}
                                        </TableCell>
                                        <TableCell className="text-muted-foreground align-top font-mono text-xs">
                                            {connector.baseUrl}
                                            <div>{connector.probePath}</div>
                                        </TableCell>
                                        <TableCell className="align-top text-xs">
                                            {mode?.label ?? connector.auth}
                                        </TableCell>
                                        <TableCell className="align-top">
                                            {connector.secretKeys.length === 0 ? (
                                                <span className="text-muted-foreground text-xs">None stored</span>
                                            ) : (
                                                <div className="flex flex-wrap gap-1">
                                                    {/* Names, which is all the API returns. There is no
                                                        value here to show or to send back. */}
                                                    {connector.secretKeys.map((key) => (
                                                        <Badge key={key} variant="secondary" className="text-[11px]">
                                                            {key}
                                                        </Badge>
                                                    ))}
                                                </div>
                                            )}
                                        </TableCell>
                                        <TableCell className="align-top">
                                            <ProbeCell connector={connector} />
                                        </TableCell>
                                        <TableCell className="align-top">
                                            <div className="flex items-center gap-1">
                                                <Button
                                                    type="button"
                                                    variant="ghost"
                                                    size="icon-sm"
                                                    aria-label={`Test ${connector.name}`}
                                                    disabled={testing === connector.slug}
                                                    onClick={() => void onTest(connector)}
                                                >
                                                    <IconBolt className="size-3.5" />
                                                </Button>
                                                <Button
                                                    type="button"
                                                    variant="ghost"
                                                    size="icon-sm"
                                                    aria-label={`Edit ${connector.name}`}
                                                    onClick={() => openEdit(connector)}
                                                >
                                                    <IconPen className="size-3.5" />
                                                </Button>
                                                <ConfirmDialog
                                                    title={`Delete ${connector.name}?`}
                                                    description={`Its stored credentials go with it, in the same transaction. Any request definition or workflow naming "${connector.slug}" will stop working, and re-adding it means entering the credentials again.`}
                                                    confirmLabel="Delete"
                                                    destructive
                                                    onConfirm={() => void onDelete(connector)}
                                                    trigger={
                                                        <Button
                                                            type="button"
                                                            variant="ghost"
                                                            size="icon-sm"
                                                            aria-label={`Delete ${connector.name}`}
                                                            className="text-destructive hover:text-destructive"
                                                        >
                                                            <IconTrash className="size-3.5" />
                                                        </Button>
                                                    }
                                                />
                                            </div>
                                        </TableCell>
                                    </TableRow>
                                );
                            })}
                        </TableBody>
                    </Table>
                </div>
            )}

            {dialogOpen && (
                // Keyed so opening a different connector starts a fresh form rather than showing the
                // last one's values, and so a cancelled edit leaves nothing behind.
                <ConnectorDialog
                    key={editing?.id ?? 'new'}
                    connector={editing}
                    open={dialogOpen}
                    onOpenChange={setDialogOpen}
                />
            )}
        </>
    );
}
