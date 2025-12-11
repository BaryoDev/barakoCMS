# Tutorial: Your First Content Type

In this tutorial, we will create a **"BlogPost"** content type and publish our first article.

## Step 1: Define the Content Type

Content Types are defined dynamically via the API. No database migrations are needed!

**Request:**
```bash
POST /api/content-types
Authorization: Bearer <TOKEN>

{
  "name": "Blog Post",
  "description": "Standard blog articles",
  "fields": {
    "title": "string",
    "slug": "string",
    "body": "richtext",
    "coverImage": "string",
    "tags": "array",
    "isFeatured": "boolean"
  }
}
```

**Response:**
```json
{
  "slug": "blog-post",
  "message": "Content Type 'Blog Post' created successfully."
}
```

## Step 2: Create a Draft

Now let's create a post using the schema we just defined.

**Request:**
```bash
POST /api/contents
{
  "contentType": "blog-post",
  "data": {
    "title": "Hello World",
    "slug": "hello-world",
    "body": "<h1>Welcome</h1><p>This is my first post.</p>",
    "isFeatured": true,
    "tags": ["cms", "dotnet"]
  }
}
```

**Response:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": "Draft",
  "version": 1
}
```

## Step 3: Publish It

Content is created as `Draft (0)` by default. Let's publish it (`1`).

**Request:**
```bash
PUT /api/contents/550e8400-e29b-41d4-a716-446655440000/status
{
  "newStatus": 1
}
```

## Step 4: Fetch It

Public APIs can now retrieve this content.

**Request:**
```bash
GET /api/contents?contentType=blog-post&status=Published
```

**Response:**
```json
[
  {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "data": {
      "title": "Hello World",
      ...
    }
  }
]
```

🎉 **Success!** You have defined a schema, created content, and published it—all via API.
