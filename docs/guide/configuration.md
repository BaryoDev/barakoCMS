# Configuration Reference

BarakoCMS is configured via `appsettings.json` and Environment Variables.

## Core Settings

| Setting                               | Type   | Description                                                 |
| :------------------------------------ | :----- | :---------------------------------------------------------- |
| `ConnectionStrings:DefaultConnection` | string | **Required**. PostgreSQL connection string.                 |
| `JWT:Key`                             | string | **Required**. Secret key for signing tokens (min 32 chars). |
| `JWT:Issuer`                          | string | Token issuer (default: "BarakoCMS").                        |
| `JWT:Audience`                        | string | Token audience (default: "BarakoCMS").                      |
| `BaseUrl`                             | string | Public URL of the API (for email links).                    |

## Feature Flags

| Setting                      | Type | Default | Description                            |
| :--------------------------- | :--- | :------ | :------------------------------------- |
| `Features:UseAsyncWorkflows` | bool | `true`  | Enable background workflow processing. |
| `Features:EnableOpenApi`     | bool | `false` | Enable Swagger UI in Production.       |

## Validation Settings

| Setting             | Type | Default | Description                            |
| :------------------ | :--- | :------ | :------------------------------------- |
| `Validation:Strict` | bool | `true`  | Reject unknown fields in JSON payload. |

## Environment Variables

For production (e.g., Docker, Azure), override settings using double underscores:

```bash
# Override Connection String
export ConnectionStrings__DefaultConnection="Host=prod-db;..."

# Override JWT Key
export JWT__Key="vERY-sECRET-kEY-tHAT-iS-lONG-eNOUGH-123!"
```
