'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { toast } from 'sonner';
import {
  useCreateWorkflow,
  useValidateWorkflow,
  useWorkflowActions,
  useWorkflowVariables,
} from '@/hooks/use-workflows';
import { useSchemas } from '@/hooks/use-schemas';
import { apiErrorMessage } from '@/lib/api';
import type { TriggerEvent, WorkflowAction, WorkflowDefinition } from '@/types/workflow';
import { PageHeader } from '@/components/patterns/page-header';
import { ActionIcon } from '@/components/workflow/action-icon';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Separator } from '@/components/ui/separator';
import { Badge } from '@/components/ui/badge';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { IconCheckCircle, IconPlus, IconTrash, IconWarning } from '@/components/icons';

export default function NewWorkflowPage() {
  const router = useRouter();
  const { data: schemas } = useSchemas();
  const { data: actionTypes } = useWorkflowActions();
  const createWorkflow = useCreateWorkflow();
  const validateWorkflow = useValidateWorkflow();

  const [name, setName] = useState('');
  const [triggerContentType, setTriggerContentType] = useState('');
  const [triggerEvent, setTriggerEvent] = useState<TriggerEvent>('Created');
  const [conditions, setConditions] = useState<{ field: string; value: string }[]>([]);
  const [actions, setActions] = useState<WorkflowAction[]>([]);

  const { data: variables } = useWorkflowVariables(triggerContentType || undefined);

  const definition = (): WorkflowDefinition => ({
    name,
    triggerContentType,
    triggerEvent,
    conditions: Object.fromEntries(
      conditions.filter((c) => c.field.trim()).map((c) => [c.field.trim(), c.value])
    ),
    actions,
  });

  const canSave = name.trim() && triggerContentType && actions.length > 0;

  const addAction = (type: string) => {
    const meta = actionTypes?.find((a) => a.type === type);
    const parameters = Object.fromEntries((meta?.requiredParameters ?? []).map((p) => [p, '']));
    setActions((prev) => [...prev, { type, parameters }]);
  };

  const setActionParam = (index: number, key: string, value: string) => {
    setActions((prev) =>
      prev.map((a, i) => (i === index ? { ...a, parameters: { ...a.parameters, [key]: value } } : a))
    );
  };

  const validate = () => {
    validateWorkflow.mutate(definition(), {
      onSuccess: (result) => {
        if (result.isValid) toast.success('The workflow is valid');
        else
          toast.error(
            `Fix before saving: ${result.errors.map((e) => `${e.field}: ${e.message}`).join('; ')}`
          );
      },
      onError: (error) => toast.error(apiErrorMessage(error, 'Validation could not run.')),
    });
  };

  const save = () => {
    createWorkflow.mutate(definition(), {
      onSuccess: () => {
        toast.success(`Workflow “${name}” created`);
        router.push('/workflows');
      },
      onError: (error) => toast.error(apiErrorMessage(error, 'The workflow could not be created.')),
    });
  };

  return (
    <>
      <PageHeader
        title="New workflow"
        description="Runs after an entry of the chosen type is created or updated — it never blocks saving."
      />

      <div className="max-w-2xl space-y-6">
        <div className="space-y-2">
          <Label htmlFor="wf-name">Name</Label>
          <Input
            id="wf-name"
            value={name}
            placeholder="Notify the team on publish"
            onChange={(e) => setName(e.target.value)}
          />
        </div>

        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          <div className="space-y-2">
            <Label>When an entry of type…</Label>
            <Select value={triggerContentType} onValueChange={setTriggerContentType}>
              <SelectTrigger className="w-full">
                <SelectValue placeholder="Choose a content type" />
              </SelectTrigger>
              <SelectContent>
                {schemas?.map((s) => (
                  <SelectItem key={s.name} value={s.name}>
                    {s.displayName}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-2">
            <Label>…is</Label>
            <Select value={triggerEvent} onValueChange={(v) => setTriggerEvent(v as TriggerEvent)}>
              <SelectTrigger className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Created">Created</SelectItem>
                <SelectItem value="Updated">Updated</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <Separator />

        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-medium">Only when</h3>
              <p className="text-muted-foreground text-xs">
                Optional field conditions — the workflow runs only if every one matches.
              </p>
            </div>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setConditions((prev) => [...prev, { field: '', value: '' }])}
            >
              <IconPlus />
              Add condition
            </Button>
          </div>
          {conditions.map((condition, i) => (
            <div key={i} className="flex items-center gap-2">
              <Input
                placeholder="Field, e.g. Status"
                value={condition.field}
                className="font-mono text-xs"
                onChange={(e) =>
                  setConditions((prev) =>
                    prev.map((c, j) => (j === i ? { ...c, field: e.target.value } : c))
                  )
                }
              />
              <span className="text-muted-foreground text-sm">equals</span>
              <Input
                placeholder="Value"
                value={condition.value}
                className="font-mono text-xs"
                onChange={(e) =>
                  setConditions((prev) =>
                    prev.map((c, j) => (j === i ? { ...c, value: e.target.value } : c))
                  )
                }
              />
              <Button
                type="button"
                variant="ghost"
                size="icon"
                aria-label="Remove condition"
                onClick={() => setConditions((prev) => prev.filter((_, j) => j !== i))}
              >
                <IconTrash className="size-3.5" />
              </Button>
            </div>
          ))}
        </div>

        <Separator />

        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-medium">Then do</h3>
              <p className="text-muted-foreground text-xs">
                Actions run in order. Parameters accept {'{{variables}}'} from the entry.
              </p>
            </div>
            <Select value="" onValueChange={addAction}>
              <SelectTrigger size="sm" className="w-40">
                <SelectValue placeholder="Add an action" />
              </SelectTrigger>
              <SelectContent>
                {actionTypes?.map((meta) => (
                  <SelectItem key={meta.type} value={meta.type}>
                    {meta.type}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {actions.length === 0 && (
            <p className="text-muted-foreground rounded-lg border border-dashed px-4 py-6 text-center text-sm">
              No actions yet. A workflow needs at least one.
            </p>
          )}

          {actions.map((action, i) => {
            const meta = actionTypes?.find((a) => a.type === action.type);
            return (
              <div key={i} className="space-y-3 rounded-lg border p-4">
                <div className="flex items-center justify-between">
                  <span className="flex items-center gap-2 text-sm font-medium">
                    <ActionIcon type={action.type} className="text-primary size-4" />
                    {i + 1}. {action.type}
                  </span>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    aria-label={`Remove ${action.type} action`}
                    onClick={() => setActions((prev) => prev.filter((_, j) => j !== i))}
                  >
                    <IconTrash className="size-3.5" />
                  </Button>
                </div>
                {meta?.description && (
                  <p className="text-muted-foreground text-xs">{meta.description}</p>
                )}
                {Object.keys(action.parameters).map((param) => (
                  <div key={param} className="space-y-1.5">
                    <Label htmlFor={`action-${i}-${param}`} className="text-xs">
                      {param}
                    </Label>
                    <Input
                      id={`action-${i}-${param}`}
                      value={action.parameters[param]}
                      className="font-mono text-xs"
                      onChange={(e) => setActionParam(i, param, e.target.value)}
                    />
                  </div>
                ))}
              </div>
            );
          })}

          {variables && (variables.systemVariables.length > 0 || variables.dataFields.length > 0) && (
            <details className="text-sm">
              <summary className="text-muted-foreground cursor-pointer">Available template variables</summary>
              <div className="mt-2 flex flex-wrap gap-1.5">
                {[...variables.systemVariables, ...variables.dataFields].map((variable) => (
                  <Badge
                    key={variable.name}
                    variant="secondary"
                    className="font-mono font-normal"
                    title={variable.description}
                  >
                    {variable.name}
                  </Badge>
                ))}
              </div>
            </details>
          )}
        </div>

        <div className="flex items-center gap-2">
          <Button onClick={save} disabled={!canSave || createWorkflow.isPending}>
            {createWorkflow.isPending ? 'Creating…' : 'Create workflow'}
          </Button>
          <Button
            variant="outline"
            onClick={validate}
            disabled={!canSave || validateWorkflow.isPending}
          >
            {validateWorkflow.data?.isValid ? (
              <IconCheckCircle className="text-success size-3.5" />
            ) : validateWorkflow.data ? (
              <IconWarning className="text-warning size-3.5" />
            ) : null}
            Validate first
          </Button>
          <Button variant="ghost" onClick={() => router.push('/workflows')}>
            Cancel
          </Button>
        </div>
      </div>
    </>
  );
}
