# Why BarakoCMS is Now "One-Click" Ready with Docker Hub 🚀

*December 19, 2025 • By Arnel Robles (BaryoDev)*

For a long time, BarakoCMS was a tool designed for developers who didn't mind getting their hands dirty with source code, `npm install`, and `.NET build` commands. While we love that flexibility, our vision has always been to bridge the gap between "Developer Tool" and "SMB Product."

Today, we take a massive leap towards that vision.

## 📦 Official Docker Hub Images are Here

We are excited to announce that **BarakoCMS is now officially available on Docker Hub**! You can now pull our pre-built images and have a full-stack CMS running in seconds.

- **Backend API**: `arnelirobles/barako-cms:latest`
- **Admin UI**: `arnelirobles/barako-admin:latest`

## The "Just Run It" Experience

The biggest challenge with SPA (Single Page Application) Docker images is environment configuration. Usually, you have to choose your API URL at *build time*, which means a generic image from Docker Hub wouldn't work for everyone.

**Not anymore.**

We've implemented a **Runtime Configuration** mechanism. When you start the BarakoCMS Admin container, a lightweight entrypoint script injects your specific environment variables directly into the browser.

### What this means for you:
You don't need our source code to run BarakoCMS. You just need a simple `docker-compose.yml` file:

```yaml
services:
  app:
    image: arnelirobles/barako-cms:latest
    ports: ["5005:8080"]
  
  admin:
    image: arnelirobles/barako-admin:latest
    environment:
      - NEXT_PUBLIC_API_URL=http://your-server-ip:5005
    ports: ["3000:3000"]
```

## Why this matters for the "Baryo"
This is a core part of our **Phase 2.6: SMB Enablement** roadmap. By making the deployment "zero-compilation," we're making it possible for small businesses and non-technical users to host their own powerful, AI-native CMS on any cloud provider or even a local server with minimal effort.

## What's Next?
This infrastructure hardening paves the way for our **AI-Native (MCP)** integrations and the **1-Click Cloud Deploy** buttons for Railway and Render.

Go ahead, `docker pull` your way into the future of headless CMS!

---
*Stay caffeinated,*
**Arnel Robles**
Found of BaryoDev
