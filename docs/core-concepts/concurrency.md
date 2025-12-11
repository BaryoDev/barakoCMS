# Optimistic Concurrency Control

In a collaborative environment (like a CMS), multiple users might try to edit the same record simultaneously. BarakoCMS uses **Optimistic Concurrency** to prevent data loss.

## The Problem: The "Lost Update"

1.  **Alice** reads Article A (Version 1).
2.  **Bob** reads Article A (Version 1).
3.  **Alice** saves changes. Article becomes **Version 2**.
4.  **Bob** saves changes based on Version 1.
5.  *Result*: Alice's changes are overwritten by Bob. 😱

## The Solution: Version Checks

We require the client to send the `version` they are modifying.

```bash
PUT /api/contents/{id}
{
  "data": { ... },
  "version": 1      // Bob sends this
}
```

If the database is currently at **Version 2** (because of Alice), the server compares `1 != 2` and rejects the request.

## Handling Conflicts (412)

If you receive a `412 Precondition Failed` error:

1.  **Inform the User**: "This content has been modified by another user."
2.  **Refresh**: Fetch the latest version (`GET /api/contents/{id}`).
3.  **Merge/Retry**: The user acts on the *new* data and tries saving again (now sending `version: 2`).

## Why not Locking?

"Pessimistic Locking" (locking the row) causes performance bottlenecks and deadlocks. Optimistic Concurrency is:
*   **Stateless**: No server memory used for locks.
*   **Scalable**: Works across load balancers.
*   **Safe**: Guarantees data integrity.
