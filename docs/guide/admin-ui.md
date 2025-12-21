# Admin UI

BarakoCMS includes a **full-featured Admin Dashboard** built with Next.js 15, providing a modern, intuitive interface for content management.

## Features

| Feature                | Description                                                      |
| ---------------------- | ---------------------------------------------------------------- |
| **Dashboard**          | Health status, quick stats, system overview                      |
| **Content Management** | Create, Edit, List, Search, Filter content entries               |
| **Schema Management**  | Define and view Content Types with field definitions             |
| **Workflows**          | Create and manage automation rules                               |
| **Roles & UserGroups** | RBAC administration with granular permissions                    |
| **Operations**         | Health Checks, Logs, Backups (Create, Download, Restore, Delete) |

## Getting Started

### Prerequisites
- Node.js 18+
- npm or yarn
- BarakoCMS backend running

### Installation

```bash
cd admin
npm install
```

### Development

```bash
npm run dev
```

Open **Admin Dashboard**: `http://localhost:3000`

### Production Build

```bash
npm run build
npm start
```

## Default Credentials

| Username | Password      | Role       |
| -------- | ------------- | ---------- |
| `arnex`  | `password123` | SuperAdmin |

::: warning
Change default credentials in production!
:::

## UI Components

The Admin UI uses [shadcn/ui](https://ui.shadcn.com/) components built on Radix UI:

- **AlertDialog** - Confirmation dialogs for destructive actions
- **Tooltip** - Contextual hints on hover
- **Skeleton** - Loading state placeholders
- **Breadcrumb** - Navigation context
- **Toast** - Success/error notifications via Sonner

## Pages Overview

### Dashboard (`/`)
Central hub with:
- Content Types count
- Content Items count
- Infrastructure status
- System health with progress bars
- Quick action cards

### Content (`/content`)
- Search and filter content by type
- Table view with status badges
- Edit/Preview actions with tooltips

### Schemas (`/schemas`)
- List all content types
- Preview, Edit, Duplicate actions
- Field count display

### Workflows (`/workflows`)
- Card-based workflow display
- State flow visualization
- Trigger type badges

### Backups (`/ops/backups`)
- Create system snapshots
- Download backups
- Restore from backup (with confirmation)
- Delete backups (with confirmation)

## Environment Variables

```bash
# admin/.env.local
NEXT_PUBLIC_API_URL=http://localhost:5006
```

## Security

The Admin UI requires JWT authentication. All API calls include the Bearer token automatically via Axios interceptors.

::: tip
The backend enforces RBAC. Only SuperAdmin users can access backup operations.
:::
