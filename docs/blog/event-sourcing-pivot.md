# The Event Sourcing Pivot: Swallowing the Hard Pill 🕰️

*December 10, 2025 • By Arnel Robles (Founder, BaryoDev)*

In the early prototypes of BarakoCMS, we used a traditional CRUD (Create, Read, Update, Delete) approach. It was easy. It was familiar. It was also... about as reliable as a weather forecast in the middle of a typhoon.

## The CRUD Nightmare

As the CMS grew, I started seeing the cracks. I’d have a "Status" field for a blog post. When a user changed it from `Draft` to `Published`, we just updated that single database row. Success! Everyone’s happy, right? 

Wrong. Because ten minutes later, someone (usually the person paying me) would ask: *“Arnel, who published this? When did they do it? And more importantly, what did the content look like before they accidentally deleted three paragraphs of my best work?”*

I tried adding a "History" table. Then an "AuditLog" table. Soon, the code to maintain the "history of the history" was more complex than the code to actually manage the content. I was basically building a digital archaeological site where every update buried the previous one in a tomb of SQL.

I realized I was fighting the nature of time itself. I needed to pivot.

## Enter Event Sourcing (The Bank Ledger of Code)

Event Sourcing is the idea that the "Source of Truth" isn't the current state of an object (the "what"), but the sequence of events that led to it (the "how" and the "when"). 

Imagine your bank account. Traditional CRUD only stores the balance: `$100`. If that number changes to `$120`, you have no idea why. Event Sourcing stores the transactions: `+ $50 (Salary)`, `- $10 (Coffee)`, `+ $80 (Birthday Cake)`. 

You can always calculate the balance from the transactions, but you can *never* reconstruct the transactions from just the balance. CRUD is a snapshot; Event Sourcing is a documentary.

## Swallowing the Pill: The Marten Choice

Switching to Event Sourcing mid-stream was what we in the biz call a "Hard Pill to Swallow." It’s like deciding to change your car's engine while you're driving at 60mph. It required rethinking our entire data access layer.

But then I found **Marten**. Marten allowed us to use **PostgreSQL** as a high-performance event store. It gave us the best of both worlds: the reliability of a battle-tested SQL database and the flexibility of a document store. It was the sugar that made the medicine go down.

## The Payoff: Indestructible Data

Because BarakoCMS is built on Event Sourcing, your data is now effectively "indestructible."
- If we mess up a projection (the view of the data), we don't panic. We delete it and "replay" the events to rebuild it perfectly.
- If a user asks "what happened here?", we have a millisecond-by-millisecond record. No more "He said, She said."
- We have "Time Travel"—built-in versioning that is mathematically guaranteed to be accurate.

## Was it worth it?

After 15 years in the trenches, I can tell you that shortcuts are like low-quality coffee—they give you a quick burst of energy, but the crash is inevitable and bitter. The pivot to Event Sourcing was the single most difficult architectural decision we made, but it’s the one that lets me sleep at night.

***

### 🌿 Life Lesson from the Baryo
In the baryo, we remember our elders and our history, because without knowing where we came from, we don't know where we are. Your past isn't a burden; it's the foundation of your present. Don't be afraid to keep a "ledger" of your mistakes—they are the transactions that build the balance of your wisdom.

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
