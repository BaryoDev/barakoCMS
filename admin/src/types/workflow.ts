export interface WorkflowState {
    name: string;
    displayName: string;
    color?: string;
    isInitial?: boolean;
    isFinal?: boolean;
}

export interface WorkflowTransition {
    fromState: string;
    toState: string;
    trigger: string;
    allowedRoles: string[];
}

export interface Workflow {
    id: string;
    name: string;
    contentType: string; // The content type slug this workflow applies to
    states: WorkflowState[];
    transitions: WorkflowTransition[];
}

export interface CreateWorkflowRequest {
    name: string;
    contentType: string;
    states: WorkflowState[];
    transitions: WorkflowTransition[];
}
