import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { Workflow, CreateWorkflowRequest } from '@/types/workflow';

// Fetch all workflows
export function useWorkflows() {
    return useQuery({
        queryKey: ['workflows'],
        queryFn: async () => {
            const response = await api.get<Workflow[]>('/api/workflows');
            return response.data;
        },
    });
}

// Fetch single workflow
export function useWorkflow(id: string) {
    return useQuery({
        queryKey: ['workflows', id],
        queryFn: async () => {
            const response = await api.get<Workflow>(`/api/workflows/${id}`);
            return response.data;
        },
        enabled: !!id,
    });
}

// Create workflow
export function useCreateWorkflow() {
    const queryClient = useQueryClient();
    return useMutation({
        mutationFn: async (data: CreateWorkflowRequest) => {
            const response = await api.post<Workflow>('/api/workflows', data);
            return response.data;
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['workflows'] });
        },
    });
}
