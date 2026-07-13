import type { ComponentType, SVGProps } from 'react';
import {
  IconBolt,
  IconConditional,
  IconEnvelope,
  IconFieldEdit,
  IconSms,
  IconTasks,
  IconWebhook,
} from '@/components/icons';

const ACTION_ICONS: Record<string, ComponentType<SVGProps<SVGSVGElement>>> = {
  Email: IconEnvelope,
  SMS: IconSms,
  Webhook: IconWebhook,
  CreateTask: IconTasks,
  UpdateField: IconFieldEdit,
  Conditional: IconConditional,
};

export function ActionIcon({ type, className }: { type: string; className?: string }) {
  const Icon = ACTION_ICONS[type] ?? IconBolt;
  return <Icon className={className} />;
}
