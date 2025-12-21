# Why BarakoCMS is Now "One-Click" Ready with Docker Hub 🚀

*December 19, 2025 • By Arnel Robles (Founder, BaryoDev)*

For a long time, BarakoCMS was primarily a tool for developers—those who didn't mind getting their hands dirty with source code, managing `npm` dependencies, or running `.NET` build commands. While we value that flexibility, our long-term vision has always been to bridge the gap between a "Developer-First Tool" and a "Production-Ready SMB Product."

Today, we are taking a significant step toward making that vision a reality.

## 📦 Official Docker Hub Images are Here

We are excited to announce that **BarakoCMS is now officially available on Docker Hub**! You can now pull our pre-built, production-ready images and have a full-stack headless CMS running in seconds.

- **Backend API**: `arnelirobles/barako-cms:latest`
- **Admin UI**: `arnelirobles/barako-admin:latest`

## The "Just Run It" Experience

One of the biggest hurdles with deploying Single Page Application (SPA) Docker images is environment configuration. Typically, you have to define your API URL at *build time*, which means a generic image from Docker Hub wouldn't work for different server environments without a complete rebuild.

**We’ve solved that.**

We implemented a **Runtime Configuration** mechanism. When you start the BarakoCMS Admin container, a lightweight entrypoint script automatically injects your specific environment variables directly into the browser context.

### What this means for you:
You no longer need the source code to run BarakoCMS. You just need a simple `docker-compose.yml` file:

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

## Empowering the "Baryo"
This release is a cornerstone of our **Phase 2.6: SMB Enablement** roadmap. By shifting to a "zero-compilation" deployment model, we are making it possible for small businesses and independent developers to host their own powerful, AI-native CMS on any cloud provider—or even a local server—with minimal technical overhead.

## What's Next?
This infrastructure hardening paves the way for our upcoming **AI-Native (MCP)** integrations and "One-Click" deployment buttons for platforms like Railway and Render.

Ready to simplify your stack? `docker pull` your way into the future of headless CMS!

For more detailed setup instructions, check out our [Local Deployment Guide](/guide/local-deployment).

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
