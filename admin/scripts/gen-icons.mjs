// Generates src/components/icons/index.tsx from line-awesome (Icons8) SVGs.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const SVG_DIR = '/Users/arnelirobles/barakoCMS/admin/node_modules/line-awesome/svg';
const OUT = '/Users/arnelirobles/barakoCMS/admin/src/components/icons/index.tsx';

// semantic component name -> line-awesome file
const ICONS = {
  Dashboard: 'tachometer-alt-solid',
  ContentTypes: 'shapes-solid',
  Content: 'file-alt-solid',
  Workflows: 'project-diagram-solid',
  Users: 'users-solid',
  Roles: 'user-shield-solid',
  Groups: 'users-cog-solid',
  Health: 'heartbeat-solid',
  Settings: 'cog-solid',
  User: 'user-solid',
  UserPlus: 'user-plus-solid',
  SignOut: 'sign-out-alt-solid',
  SignIn: 'sign-in-alt-solid',
  Plus: 'plus-solid',
  Search: 'search-solid',
  Pen: 'pen-solid',
  Trash: 'trash-solid',
  Eye: 'eye-solid',
  EyeSlash: 'eye-slash',
  Check: 'check-solid',
  Times: 'times-solid',
  History: 'history-solid',
  Rollback: 'undo-alt-solid',
  Bolt: 'bolt-solid',
  Envelope: 'envelope-solid',
  Sms: 'sms-solid',
  Webhook: 'globe-solid',
  Tasks: 'tasks-solid',
  FieldEdit: 'edit-solid',
  Conditional: 'code-branch-solid',
  Play: 'play-solid',
  Bug: 'bug-solid',
  Shield: 'shield-alt-solid',
  Key: 'key-solid',
  Database: 'database-solid',
  Server: 'server-solid',
  Memory: 'memory-solid',
  Disk: 'hdd-solid',
  Sun: 'sun-solid',
  Moon: 'moon-solid',
  More: 'ellipsis-h-solid',
  ArrowRight: 'arrow-right-solid',
  ArrowLeft: 'arrow-left-solid',
  Copy: 'copy-solid',
  Filter: 'filter-solid',
  Calendar: 'calendar-solid',
  Clock: 'clock-solid',
  Info: 'info-circle-solid',
  Warning: 'exclamation-triangle-solid',
  CheckCircle: 'check-circle-solid',
  TimesCircle: 'times-circle-solid',
  Refresh: 'sync-alt-solid',
  Archive: 'archive-solid',
  Lock: 'lock-solid',
  Unlock: 'unlock-solid',
  Toggle: 'toggle-on-solid',
  Hashtag: 'hashtag-solid',
  Text: 'font-solid',
  List: 'list-solid',
  Cube: 'cube-solid',
  Table: 'table-solid',
  ChevronRight: 'chevron-right-solid',
  ChevronDown: 'chevron-down-solid',
  ChevronLeft: 'chevron-left-solid',
  ExternalLink: 'external-link-alt-solid',
  Send: 'paper-plane-solid',
  Coffee: 'coffee-solid',
  Mug: 'mug-hot-solid',
};

let out = `// Icon set: Line Awesome by Icons8 — https://icons8.com/line-awesome (MIT-style free license)
// Vendored as inline SVG React components (32x32 viewBox, currentColor fill).
// Regenerate with scripts/gen-icons.mjs if you need more glyphs.
import type { SVGProps } from 'react';

type IconProps = SVGProps<SVGSVGElement>;

function base(props: IconProps) {
  return {
    xmlns: 'http://www.w3.org/2000/svg',
    viewBox: '0 0 32 32',
    width: '1em',
    height: '1em',
    fill: 'currentColor',
    'aria-hidden': true as const,
    ...props,
  };
}

`;

for (const [name, file] of Object.entries(ICONS)) {
  const svg = readFileSync(join(SVG_DIR, `${file}.svg`), 'utf8');
  const inner = svg.replace(/^<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '').trim();
  out += `export function Icon${name}(props: IconProps) {\n  return <svg {...base(props)}>${inner}</svg>;\n}\n\n`;
}

writeFileSync(OUT, out);
console.log(`wrote ${OUT} with ${Object.keys(ICONS).length} icons`);
