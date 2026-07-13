import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
    WorkflowDefinition,
    WorkflowActionMetadata,
    TemplateVariableCollection,
    WorkflowValidationResult,
    WorkflowExecutionLog,
    DryRunResult,
} from '@/types/workflow';

export function useWorkflows() {
    return useQuery({
        queryKey: ['workflows'],
        queryFn: async () => {
            const response = await api.get<WorkflowDefinition[]>('/api/workflows');
            return response.data;
        },
    });
}

// The backend has no single-workflow endpoint; select from the cached list.
export function useWorkflow(id: string) {
    const query = useWorkflows();
    return {
        ...query,
        data: query.data?.find((w) => w.id === id),
    };
}

export function useWorkflowActions() {
    return useQuery({
        queryKey: ['workflow-actions'],
        queryFn: async () => {
            const response = await api.get<WorkflowActionMetadata[]>('/api/workflows/actions');
            return response.data;
        },
        staleTime: Infinity, // plugin list only changes with a backend deploy
    });
}

export function useWorkflowVariables(contentType?: string) {
    return useQuery({
        queryKey: ['workflow-variables', contentType],
        queryFn: async () => {
            const response = await api.get<TemplateVariableCollection>('/api/workflows/variables', {
                params: contentType ? { contentType } : {},
            });
            return response.data;
        },
    });
}

export function useWorkflowDebugLogs(id: string, limit = 20) {
    return useQuery({
        queryKey: ['workflow-debug', id, limit],
        queryFn: async () => {
            const response = await api.get<WorkflowExecutionLog[]>(`/api/workflows/${id}/debug`, {
                params: { limit },
            });
            return response.data;
        },
        enabled: !!id,
    });
}

export function useCreateWorkflow() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (data: WorkflowDefinition) => {
            const response = await api.post<WorkflowDefinition>('/api/workflows', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['workflows'] });
        },
    });
}

export function useValidateWorkflow() {
    return useMutation({
        mutationFn: async (data: WorkflowDefinition) => {
            const response = await api.post<WorkflowValidationResult>('/api/workflows/validate', data);
            return response.data;
        },
    });
}

export function useDryRunWorkflow() {
    return useMutation({
        mutationFn: async (payload: { workflow: WorkflowDefinition; sampleContent: unknown }) => {
            const response = await api.post<DryRunResult>('/api/workflows/dry-run', payload);
            return response.data;
        },
    });
}
