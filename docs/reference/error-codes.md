# API Error Codes

BarakoCMS uses standard HTTP status codes.

| Code    | Meaning               | Context                                                         |
| :------ | :-------------------- | :-------------------------------------------------------------- |
| **200** | OK                    | Request succeeded.                                              |
| **201** | Created               | Resource created successfully.                                  |
| **204** | No Content            | Action succeeded but returns no data.                           |
| **400** | Bad Request           | Validation failed. See `errors` array in response.              |
| **401** | Unauthorized          | Missing or invalid JWT token.                                   |
| **403** | Forbidden             | Valid token, but insufficient RBAC permissions.                 |
| **404** | Not Found             | Resource ID does not exist.                                     |
| **409** | Conflict              | Duplicate request (Idempotency) or unique constraint violation. |
| **412** | Precondition Failed   | Optimistic Concurrency conflict (Version mismatch).             |
| **500** | Internal Server Error | Something went wrong on the server. Check logs.                 |

## Problem Details Format

Errors are returned in [RFC 7807](https://tools.ietf.org/html/rfc7807) format:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Data.Email": [
      "'Email' is not a valid email address."
    ]
  }
}
```
