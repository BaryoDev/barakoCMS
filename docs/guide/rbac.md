# Role-Based Access Control (RBAC)

BarakoCMS v2.0 introduces an advanced RBAC system with granular permissions and dynamic conditions.

## Concepts

- **Roles**: Collections of permissions (e.g., "Editor", "HR Manager").
- **UserGroups**: Organized groups of users (e.g., "Marketing Team").
- **Permissions**: Granular control (`Create`, `Read`, `Update`, `Delete`) per Content Type.
- **System Capabilities**: Global flags for non-content actions (e.g., `view_analytics`).

## API Endpoints

### Role Management
```bash
POST   /api/roles                  # Create role
GET    /api/roles                  # List roles
GET    /api/roles/{id}             # Get role
PUT    /api/roles/{id}             # Update role
DELETE /api/roles/{id}             # Delete role
```

### UserGroup Management
```bash
POST   /api/user-groups            # Create group
GET    /api/user-groups            # List groups
GET    /api/user-groups/{id}       # Get group
PUT    /api/user-groups/{id}       # Update group
DELETE /api/user-groups/{id}       # Delete group
POST   /api/user-groups/{id}/users # Add user to group
DELETE /api/user-groups/{id}/users/{userId} # Remove user
```

## Example: Creating a Restricted Role

This example creates a "Content Editor" role that can only edit their *own* articles.

```bash
POST /api/roles
Authorization: Bearer {ADMIN_TOKEN}

{
  "name": "Content Editor",
  "description": "Can edit own articles",
  "permissions": [{
    "contentTypeSlug": "article",
    "create": { "enabled": true },
    "read": { "enabled": true },
    "update": {
      "enabled": true,
      "conditions": { "author": { "_eq": "$CURRENT_USER" } } // Condition!
    },
    "delete": { "enabled": false }
  }]
}
```
