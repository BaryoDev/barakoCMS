// Types mirroring the backend workflow engine (Models/WorkflowDefinition.cs):
// workflows are content-event triggers that run a list of actions, not a state machine.

export interface WorkflowAction {
    type: string; // Email | SMS | Webhook | CreateTask | UpdateField | Conditional
    parameters: Record<string, string>; // values support {{template}} variables
}

export interface WorkflowDefinition {
    id?: string;
    name: string;
    triggerContentType: string;
    triggerEvent: TriggerEvent;
    conditions: Record<string, string>;
    actions: WorkflowAction[];
}

export type TriggerEvent = 'Created' | 'Updated';

export interface WorkflowActionMetadata {
    type: string;
    description: string;
    requiredParameters: string[];
    exampleConfiguration: string;
}

export interface TemplateVariable {
    name: string; // "{{status}}"
    description: string;
    example: string;
    type: string;
}

export interface TemplateVariableCollection {
    systemVariables: TemplateVariable[];
    dataFields: TemplateVariable[];
}

export interface WorkflowValidationResult {
    isValid: boolean;
    errors: { field: string; message: string }[];
}

export interface ActionExecutionLog {
    actionType: string;
    success: boolean;
    errorMessage?: string;
    resolvedParameters: Record<string, string>;
    duration: string;
}

export interface WorkflowExecutionLog {
    id: string;
    workflowId: string;
    contentId: string;
    executedAt: string;
    isDryRun: boolean;
    success: boolean;
    duration: string;
    actions: ActionExecutionLog[];
}

export interface DryRunResult {
    success: boolean;
    actions: ActionExecutionLog[];
    duration: string;
    message: string;
}
