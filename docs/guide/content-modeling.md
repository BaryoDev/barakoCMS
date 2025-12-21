# Content Modeling & Dynamic Schemas

BarakoCMS v2.6 introduces a **Dynamic Schema Engine**, allowing you to define Content Types at runtime without writing C# classes. This is the foundation of the "Zero Code" Headless CMS experience.

## Overview

Unlike previous versions where Content Types were hardcoded C# classes, v2.6 stores schemas in the database (Postgres/Marten). This allows the Admin UI (and API consumers) to create, modify, and delete content structures on the fly.

## Content Type Definition

A **Content Type** is a blueprint for your content (e.g., `Product`, `BlogPost`, `Event`). It consists of metadata and a list of **Fields**.

### JSON Structure

```json
{
  "name": "product",              // Unique slug (e.g. used in URL)
  "displayName": "Product",       // Human readable name
  "description": "Store items",
  "fields": [
    {
      "name": "sku",
      "displayName": "SKU",
      "type": "text",             // text, number, boolean, date
      "isRequired": true,
      "validationRules": {
         "minLength": 3
      }
    }
  ]
}
```

## Managing Schemas Usage via API

Currently, schemas are managed via the REST API (Admin UI coming in Week 3).

### 1. Create a Schema

**POST** `/api/schemas`
**Auth**: Required (Admin Role)

```bash
curl -X POST http://localhost:5005/api/schemas \
  -H "Authorization: Bearer <TOKEN>" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "blog-post",
    "displayName": "Blog Post",
    "fields": [
      { "name": "title", "type": "text", "isRequired": true },
      { "name": "publishedAt", "type": "date", "isRequired": false }
    ]
  }'
```

### 2. List Schemas

**GET** `/api/schemas`
**Auth**: Admin or Editor

Returns a list of all defined content types.

## Dynamic Validation

When you create or update content via `/api/contents`, the server automatically validates your data against the defined schema.

- **Required Fields**: If `isRequired` is true, the field must be present and non-empty.
- **Data Types**:
  - `number`: Validates that the value is a valid number.
  - `boolean`: Validates true/false.
  - `date`: Validates ISO 8601 date format.

**Example Error Response (400 Bad Request):**
```json
{
  "message": "Validation Failed: Field 'Title' (title) is required."
}
```
