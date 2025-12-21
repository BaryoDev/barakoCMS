# Beyond the Dashboard: Designing the Admin Bridge 🌉

*December 18, 2025 • By Arnel Robles (Founder, BaryoDev)*

Let’s be honest: most CMS admin panels are ugly. They look like spreadsheets that someone tried to "beautify" with a generic Bootstrap theme from 2012. 

When we built the BarakoCMS Admin Dashboard, I didn't want it to just be "functional." I wanted it to feel **premium**. I wanted a space where developers and content creators actually *enjoyed* spending their time.

## The Next.js Choice

For the frontend, we chose **Next.js**. Why? Because it’s the best bridge between developer experience and user performance. It allowed us to build a high-fidelity Single Page Application (SPA) with smooth transitions, fast page loads, and a modern component architecture.

But building a "Generic" Admin UI that handles *Dynamic* Content Types is no small feat.

## The Struggle with Dynamic Routes

In BarakoCMS, every content type you create is a dynamic route. If you create a "News" schema, the admin panel needs to instantly know how to render the edit page for it. 

We spent a lot of time perfecting the dynamic routing (`admin/src/app/schemas/[name]/page.tsx`). We had to ensure that the "handshake" between the API (which describes the schema) and the Admin UI (which renders the form) was seamless and resilient to errors.

## Aesthetics are Not Optional

I’ve heard junior devs say, "It’s internal, design doesn't matter." **They’re wrong.** 

Design communicates quality. It communicates care. We used **Tailwind CSS** and **Lucide Icons** to create a clean, minimalist aesthetic that mirrors the "Barako" philosophy. No clutter. No unnecessary animations. Just clear, high-contrast interfaces that stay out of your way.

## The Handshake: Solving CORS

One of the most persistent issues we faced during development was the "Handshake" (CORS). Getting the browser to allow the Admin UI (at `admin.dev`) to talk to the API (at `api.dev`) is a classic developer headache. We solved this by making our CORS configuration environment-driven, ensuring that whether you're on `localhost` or `fly.dev`, the handshake is secure and reliable.

## A Dashboard with a Soul

We didn't build just a dashboard; we built a bridge. A bridge that connects the complex, event-sourced power of our backend with the creative needs of our users. It’s the face of BarakoCMS, and I’m proud of how it turned out.

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
