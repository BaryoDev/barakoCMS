# API Reference

BarakoCMS provides a comprehensive REST API documented via Swagger UI.

::: tip Interactive Documentation
For full interactive documentation, run the application and visit:
`http://localhost:5000/swagger`
:::

## Key Endpoints

### 🔐 Authentication

- `POST /api/auth/login`: Get JWT token

### 📄 Content

- `POST /api/contents`: Create content (supports Idempotency)
- `GET /api/contents/{id}`: Get content by ID
- `PUT /api/contents/{id}`: Update content (supports Optimistic Concurrency)
- `PUT /api/contents/{id}/status`: Publish/Archive content

### 🛡️ RBAC

- `POST /api/roles`: Create custom roles
- `POST /api/user-groups`: Manage user groups
- `POST /api/users/{id}/roles`: Assign roles to users

### ⚙️ Schema

- `POST /api/content-types`: Define new content structures dynamically
