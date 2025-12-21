# The Event Sourcing Pivot: Swallowing the Hard Pill 🕰️

*December 10, 2025 • By Arnel Robles (Founder, BaryoDev)*

In the early prototypes of BarakoCMS, we used a traditional CRUD (Create, Read, Update, Delete) approach. It was easy. It was familiar. It was also... inadequate.

## The CRUD Nightmare

As the CMS grew, I started seeing the cracks. We had a "Status" field for content. When a user changed it from `Draft` to `Published`, we just updated the database row. But then we needed to know: *Who published it? When? What did the content look like exactly 10 minutes before it was published?*

I tried adding a "History" table. Then an "AuditLog" table. Soon, the code to maintain the history was more complex than the code to manage the content itself. 

I realized I was fighting the nature of data. I needed to pivot.

## Enter Event Sourcing

Event Sourcing is the idea that the "Source of Truth" isn't the current state of an object, but the sequence of events that led to it. 

Imagine a bank account. Traditional CRUD only stores the balance: `$100`. Event Sourcing stores the transactions: `+ $50`, `- $10`, `+ $60`. You can always calculate the balance from the transactions, but you can *never* reconstruct the transactions from just the balance.

## Swallowing the Pill: The Marten Choice

Switching to Event Sourcing mid-stream was a "hard pill to swallow." It required rethinking our entire data access layer. But when I found **Marten**, the pain eased. 

Marten allowed us to use **PostgreSQL** as a high-performance event store. It gave us the best of both worlds: the reliability of a battle-tested SQL database and the flexibility of a document store.

## The Payoff: Indestructible Data

Because BarakoCMS is built on Event Sourcing, your data is now "indestructible."
- If we mess up a projection, we just delete it and replay the events to rebuild it perfectly.
- If a user asks "what happened here?", we have a millisecond-by-millisecond record of every change.
- We have "Time Travel"—built-in versioning that is mathematically guaranteed to be accurate.

## Was it worth it?

As a dev with 15 years experience, I can tell you that shortcuts always come back to haunt you. The pivot to Event Sourcing was the single most difficult architectural decision we made for BarakoCMS, but it’s the one I’m most proud of. It turned a simple tool into a professional-grade platform.

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
