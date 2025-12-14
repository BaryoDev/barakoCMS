'use client';

import { useEffect } from 'react';
import { useAuth } from '@/hooks/use-auth';
import { useSettings, useUpdateSetting } from '@/hooks/use-settings';
import { Header } from '@/components/header';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Switch } from '@/components/ui/switch';
import { Skeleton } from '@/components/ui/skeleton';
import { Settings as SettingsIcon, Monitor, FileText, Activity } from 'lucide-react';

export default function SettingsPage() {
    const { isAuthenticated, isLoading, requireAuth } = useAuth();
    const { data, isLoading: settingsLoading } = useSettings();
    const updateSetting = useUpdateSetting();

    useEffect(() => {
        requireAuth();
    }, [requireAuth]);

    if (isLoading || !isAuthenticated) {
        return null;
    }

    const handleToggle = async (key: string, currentValue: string) => {
        const newValue = currentValue === 'true' ? 'false' : 'true';
        await updateSetting.mutateAsync({ key, value: newValue });
    };

    const getSetting = (key: string) => {
        return data?.settings.find(s => s.key === key);
    };

    const getSettingValue = (key: string): boolean => {
        const setting = getSetting(key);
        return setting?.value === 'true';
    };

    const monitoringSettings = [
        { key: 'Kubernetes__Enabled', label: 'Kubernetes Monitoring', icon: Monitor },
        { key: 'HealthChecksUI__Enabled', label: 'HealthChecks UI', icon: Activity },
    ];

    const loggingSettings = [
        { key: 'Serilog__WriteToFile', label: 'File Logging', icon: FileText },
    ];

    return (
        <div className="min-h-screen bg-gradient-to-br from-slate-900 via-slate-800 to-slate-900">
            <Header />
            <main className="container mx-auto px-6 py-8">
                <div className="flex items-center gap-3 mb-8">
                    <div className="p-3 bg-cyan-500/10 rounded-lg">
                        <SettingsIcon className="h-6 w-6 text-cyan-400" />
                    </div>
                    <div>
                        <h1 className="text-3xl font-bold text-white">System Settings</h1>
                        <p className="text-slate-400">Configure runtime system behavior</p>
                    </div>
                </div>

                {/* Monitoring Section */}
                <Card className="bg-slate-800/50 border-slate-700 mb-6">
                    <CardHeader>
                        <CardTitle className="text-white flex items-center gap-2">
                            <Monitor className="h-5 w-5 text-cyan-400" />
                            Monitoring & Health Checks
                        </CardTitle>
                        <CardDescription className="text-slate-400">
                            Control monitoring features and health check components
                        </CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-4">
                        {settingsLoading ? (
                            <>
                                <Skeleton className="h-16 bg-slate-700" />
                                <Skeleton className="h-16 bg-slate-700" />
                            </>
                        ) : (
                            monitoringSettings.map(({ key, label, icon: Icon }) => {
                                const setting = getSetting(key);
                                const isEnabled = getSettingValue(key);

                                return (
                                    <div
                                        key={key}
                                        className="flex items-center justify-between p-4 bg-slate-900/50 rounded-lg border border-slate-700"
                                    >
                                        <div className="flex items-center gap-3">
                                            <Icon className="h-5 w-5 text-slate-400" />
                                            <div>
                                                <div className="text-white font-medium">{label}</div>
                                                <div className="text-sm text-slate-500">{setting?.description}</div>
                                            </div>
                                        </div>
                                        <Switch
                                            checked={isEnabled}
                                            onCheckedChange={() => handleToggle(key, setting?.value || 'false')}
                                            disabled={updateSetting.isPending}
                                        />
                                    </div>
                                );
                            })
                        )}
                    </CardContent>
                </Card>

                {/* Logging Section */}
                <Card className="bg-slate-800/50 border-slate-700">
                    <CardHeader>
                        <CardTitle className="text-white flex items-center gap-2">
                            <FileText className="h-5 w-5 text-emerald-400" />
                            Logging Configuration
                        </CardTitle>
                        <CardDescription className="text-slate-400">
                            Manage logging behavior and output destinations
                        </CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-4">
                        {settingsLoading ? (
                            <Skeleton className="h-16 bg-slate-700" />
                        ) : (
                            loggingSettings.map(({ key, label, icon: Icon }) => {
                                const setting = getSetting(key);
                                const isEnabled = getSettingValue(key);

                                return (
                                    <div
                                        key={key}
                                        className="flex items-center justify-between p-4 bg-slate-900/50 rounded-lg border border-slate-700"
                                    >
                                        <div className="flex items-center gap-3">
                                            <Icon className="h-5 w-5 text-slate-400" />
                                            <div>
                                                <div className="text-white font-medium">{label}</div>
                                                <div className="text-sm text-slate-500">{setting?.description}</div>
                                            </div>
                                        </div>
                                        <Switch
                                            checked={isEnabled}
                                            onCheckedChange={() => handleToggle(key, setting?.value || 'false')}
                                            disabled={updateSetting.isPending}
                                        />
                                    </div>
                                );
                            })
                        )}
                    </CardContent>
                </Card>
            </main>
        </div>
    );
}
