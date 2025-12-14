# BarakoCMS Blog Starter

This example demonstrates how to consume content from BarakoCMS in a simple frontend application.

## 1. Setup Content Type
Import the `blog-schema.json` into your BarakoCMS Admin Dashboard under **Content Types**.

## 2. Fetch Content
Use the following JavaScript snippet to fetch blog posts:

```javascript
const API_URL = 'http://localhost:5006';
const CONTENT_TYPE = 'blog-post';

async function fetchPosts() {
  try {
    const response = await fetch(`${API_URL}/api/contents?contentType=${CONTENT_TYPE}`);
    const data = await response.json();
    return data.items;
  } catch (error) {
    console.error('Failed to fetch posts:', error);
    return [];
  }
}

// Render posts
async function renderBlog() {
  const posts = await fetchPosts();
  const container = document.getElementById('blog-posts');
  
  posts.forEach(post => {
    const article = document.createElement('article');
    article.innerHTML = `
      <h2>${post.data.title}</h2>
      <time>${new Date(post.data.publishedAt).toLocaleDateString()}</time>
      <div>${post.data.content}</div>
    `;
    container.appendChild(article);
  });
}

renderBlog();
```

## 3. Run
Serve this directory with any static file server:
```bash
npx serve .
```
