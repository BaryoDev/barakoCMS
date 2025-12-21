# Beyond the Dashboard: Designing the Admin Bridge 🌉

*December 18, 2025 • By Arnel Robles (Founder, BaryoDev)*

Let’s be honest: most CMS admin panels are ugly. I mean, truly, functionally offensive to the eyes. They look like spreadsheets that someone tried to "beautify" with a generic Bootstrap theme from 2012, while being chased by a deadline. They’re the digital equivalent of a cluttered junk drawer—you know what you need is in there somewhere, but you’re probably going to cut your finger looking for it.

When we built the BarakoCMS Admin Dashboard, I didn't want it to just be "functional." I wanted it to feel **premium**. I wanted a space where developers and content creators actually *enjoyed* spending their time, rather than feeling like they were filling out tax forms.

## The Next.js Choice (The Fast and the Gorgeous)

For the frontend, we chose **Next.js**. Why? Because it’s the best bridge between developer experience and user performance. It allowed us to build a high-fidelity Single Page Application (SPA) with smooth transitions, fast page loads, and a modern component architecture that doesn't make me want to throw my laptop out the window.

But building a "Generic" Admin UI that handles *Dynamic* Content Types is no small feat. It’s like trying to build a suit that fits everyone regardless of their size, shape, or number of limbs.

## The Struggle with Dynamic Routes (The "Handshake" of Doom)

In BarakoCMS, every content type you create is a dynamic route. If you create a "News" schema, the admin panel needs to instantly know how to render the edit page for it. 

We spent a lot of time perfecting the dynamic routing (`admin/src/app/schemas/[name]/page.tsx`). We had to ensure that the "handshake" between the API (which describes the schema) and the Admin UI (which renders the form) was seamless and resilient. If that handshake fails, you’re not editing content; you’re staring at a white screen of despair.

## Aesthetics are Not Optional (The Baryo Sunday Rule)

I’ve heard junior devs say, "It’s internal, design doesn't matter. Just make it work." **They’re wrong.** 

In the baryo, we have the "Sunday Best" rule. You might work in the fields all week, but on Sunday, you wear your best shirt. Why? Because it communicates respect—respect for yourself and for the community. 

Design communicates quality. It communicates care. We used **Tailwind CSS** and **Lucide Icons** to create a clean, minimalist aesthetic that mirrors the "Barako" philosophy. No clutter. No unnecessary animations that make you feel like you're in a Las Vegas casino. Just clear, high-contrast interfaces that stay out of your way and let you work.

## The Handshake: Solving CORS

One of the most persistent issues we faced was the "Handshake" (CORS). Getting the browser to allow the Admin UI (at `admin.dev`) to talk to the API (at `api.dev`) is a classic developer headache that has caused more gray hairs than all my other hobbies combined. 

We solved this by making our CORS configuration environment-driven, ensuring that whether you're on `localhost` or `fly.dev`, the handshake is secure and reliable. No more "Cross-Origin" errors ruining your Tuesday afternoon.

## A Dashboard with a Soul

We didn't build just a dashboard; we built a bridge. A bridge that connects the complex, event-sourced power of our backend with the creative needs of our users. It’s the face of BarakoCMS, and I’m proud of how it turned out. It’s strong, it’s bold, and it’s beautiful—just like a good cup of coffee.

***

### 🌿 Life Lesson from the Baryo
In the baryo, we take pride in our "Sunday Best," even if no one from the city sees us. Why? Because how you do one thing is how you do everything. If you don't care about the "internal" beauty of your work, the "external" world will eventually notice the lack of soul. Take pride in the parts that no one sees—it’s where the real quality lives.

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
