# BarakoCMS Blog Starter

Reads published blog posts from the anonymous delivery API and renders them. No API key, no
login in the frontend.

Assumes barakoCMS is running on `http://localhost:5005`, which is what
[quickstart/README.md](../../quickstart/README.md) brings up with Docker.

## 1. Create the content type

There is no schema import in barakoBrew, so `blog-schema.json` is posted to the API. Sign in as an
admin first and keep the token:

```bash
API_URL=http://localhost:5005

TOKEN=$(curl -s -X POST "$API_URL/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')

curl -s -X POST "$API_URL/api/content-types" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  --data-binary @blog-schema.json
```

`isPubliclyDeliverable: true` in the schema is what puts the type on `/api/public/{type}`. Without
it that route returns 404, whatever the content underneath it says.

Prefer clicking? Build the same type under **Content Types** in barakoBrew: `blog-post`, with
`title` (text), `slug` (slug), `publishedAt` (date), `content` (richtext), `coverImage` (url) and
`tags` (array), then turn on public delivery.

## 2. Add a post and publish it

Write one in barakoBrew and publish it. Delivery serves published entries only, so a draft will not
appear no matter how the query is written.

## 3. Fetch

```javascript
const API_URL = 'http://localhost:5005';
const CONTENT_TYPE = 'blog-post';

async function fetchPosts() {
  // /api/public/{type} is anonymous. /api/contents is the authoring API and requires a bearer
  // token, so a browser calling it without one gets a 401 and no posts.
  const url = `${API_URL}/api/public/${CONTENT_TYPE}?page=1&pageSize=20&sort=-publishedAt`;
  const response = await fetch(url);

  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`);
  }

  const data = await response.json();
  return data.items;
}

async function renderBlog() {
  const container = document.getElementById('blog-posts');

  let posts;
  try {
    posts = await fetchPosts();
  } catch (error) {
    // Rendering nothing on failure is indistinguishable from having no posts, which is how the
    // earlier version of this example hid a 401 for as long as it existed.
    container.textContent = `Could not load posts: ${error.message}`;
    return;
  }

  posts.forEach(post => {
    const article = document.createElement('article');

    const heading = document.createElement('h2');
    heading.textContent = post.data.title;

    const time = document.createElement('time');
    time.textContent = new Date(post.data.publishedAt).toLocaleDateString();

    const body = document.createElement('div');
    // post.data.content is richtext the CMS stores verbatim, so it is not safe to drop into
    // innerHTML without sanitising it first.
    body.textContent = post.data.content;

    article.append(heading, time, body);
    container.appendChild(article);
  });
}

renderBlog();
```

The response is the standard paginated envelope: `items`, `page`, `pageSize`, `totalItems`,
`totalPages`, `hasNextPage`, `hasPreviousPage`. Filtering, sorting and `include` are described in
[docs/delivery-api.md](../../docs/delivery-api.md).

A single post by slug is `GET /api/public/blog-post/{slug}`.

## 4. Run

Serve this directory with any static file server:

```bash
npx serve . -l 3002
```

The API's CORS allowlist has to include whatever origin you serve from. The quickstart sets
`CORS__AllowedOrigins` to `http://localhost:3000` (where barakoBrew runs), so add the page's origin to
`ALLOWED_ORIGINS` in your `.env` and restart the API:

```ini
ALLOWED_ORIGINS=http://localhost:3000,http://localhost:3002
```

Without that the fetch fails in the browser with a CORS error while the same URL works in curl.
