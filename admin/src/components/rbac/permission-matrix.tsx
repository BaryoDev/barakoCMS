'use client';

import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Checkbox } from '@/components/ui/checkbox';
import type { ContentTypeDefinition } from '@/types/schema';

interface PermissionMatrixProps {
    schemas: ContentTypeDefinition[];
    selectedPermissions: string[];
    onChange: (permissions: string[]) => void;
}

const ACTIONS = ['create', 'read', 'update', 'delete'];

export function PermissionMatrix({ schemas, selectedPermissions, onChange }: PermissionMatrixProps) {
    const handleToggle = (schemaSlug: string, action: string) => {
        const permission = `contents:${schemaSlug}:${action}`;
        const newPermissions = selectedPermissions.includes(permission)
            ? selectedPermissions.filter(p => p !== permission)
            : [...selectedPermissions, permission];

        onChange(newPermissions);
    };

    const handleToggleAll = (schemaSlug: string) => {
        const allActions = ACTIONS.map(action => `contents:${schemaSlug}:${action}`);
        const allSelected = allActions.every(p => selectedPermissions.includes(p));

        let newPermissions = [...selectedPermissions];
        if (allSelected) {
            // Uncheck all
            newPermissions = newPermissions.filter(p => !allActions.includes(p));
        } else {
            // Check all (sanitize duplicates)
            newPermissions = [...new Set([...newPermissions, ...allActions])];
        }
        onChange(newPermissions);
    };

    return (
        <div className="border border-slate-700 rounded-md overflow-hidden">
            <Table>
                <TableHeader className="bg-slate-800">
                    <TableRow className="border-slate-700 text-slate-400 hover:bg-slate-800">
                        <TableHead className="w-[200px]">Content Type</TableHead>
                        <TableHead className="text-center w-[100px]">All</TableHead>
                        {ACTIONS.map(action => (
                            <TableHead key={action} className="text-center capitalize">{action}</TableHead>
                        ))}
                    </TableRow>
                </TableHeader>
                <TableBody>
                    {schemas.map(schema => {
                        const allActions = ACTIONS.map(action => `contents:${schema.name}:${action}`);
                        const allSelected = allActions.every(p => selectedPermissions.includes(p));

                        return (
                            <TableRow key={schema.name} className="border-slate-700 hover:bg-slate-800/50">
                                <TableCell className="font-medium text-white">
                                    {schema.displayName}
                                    <div className="text-xs text-slate-500 font-mono">{schema.name}</div>
                                </TableCell>
                                <TableCell className="text-center">
                                    <Checkbox
                                        checked={allSelected}
                                        onCheckedChange={() => handleToggleAll(schema.name)}
                                        className="border-slate-600 data-[state=checked]:bg-amber-500 data-[state=checked]:border-amber-500"
                                    />
                                </TableCell>
                                {ACTIONS.map(action => {
                                    const permission = `contents:${schema.name}:${action}`;
                                    const isChecked = selectedPermissions.includes(permission);

                                    return (
                                        <TableCell key={action} className="text-center">
                                            <Checkbox
                                                checked={isChecked}
                                                onCheckedChange={() => handleToggle(schema.name, action)}
                                                className="border-slate-600 data-[state=checked]:bg-amber-500 data-[state=checked]:border-amber-500"
                                            />
                                        </TableCell>
                                    );
                                })}
                            </TableRow>
                        );
                    })}
                </TableBody>
            </Table>
        </div>
    );
}
