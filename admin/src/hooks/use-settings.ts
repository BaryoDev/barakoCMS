import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";

export interface SystemSetting {
    key: string;
    value: string;
    description: string;
    category: string;
    updatedAt: string;
}

export interface SettingsResponse {
    settings: SystemSetting[];
}

export function useSettings() {
    return useQuery({
        queryKey: ["settings"],
        queryFn: async (): Promise<SettingsResponse> => {
            const response = await fetch(`${process.env.NEXT_PUBLIC_API_URL}/api/settings`);
            if (!response.ok) throw new Error("Failed to fetch settings");
            return response.json();
        },
    });
}

export function useUpdateSetting() {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async ({ key, value }: { key: string; value: string }) => {
            const response = await fetch(`${process.env.NEXT_PUBLIC_API_URL}/api/settings`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ key, value }),
            });

            if (!response.ok) throw new Error("Failed to update setting");
            return response.json();
        },
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ["settings"] });
        },
    });
}
