# The Road to the Cloud: Our Journey with Fly.io and Oracle Cloud ☁️

*December 19, 2025 • By Arnel Robles (Founder, BaryoDev)*

Deploying a high-performance CMS like **BarakoCMS** isn't just about pushing code and hoping for the best. It’s about understanding the infrastructure that powers it—the "pipes and wires" of the internet. Over the past few weeks, we've taken BarakoCMS to the skies with **Fly.io** and built a fortress on **Oracle Cloud**. 

It’s been a journey full of surprises, a few gray hairs, and enough logs to fill a library. Here’s the story of the hurdles we faced, how we cleared them, and what we learned along the way.

---

## Chapter 1: The Fly.io Flight (and the OOM Turbulence)

Fly.io is fantastic for developers who want to deploy globally without needing a PhD in Kubernetes. However, our initial "flight" was a bit bumpy. Imagine trying to take off in a plane, only to realize you forgot to fuel up.

### The Issue: "OOM Killed"
When we first ran `fly deploy`, our API server would crash almost immediately. The logs were screaming: `Out of Memory (OOM)`. 

BarakoCMS uses **Marten** for event sourcing, and during the initial setup, it performs a series of data seeding tasks—basically, it's busy building the foundation of your CMS. The default Fly.io machine (256MB or 512MB) was trying to do heavy lifting with "paper-thin" muscles. It just couldn't handle the initial surge.

### The Fix: Strategic Scaling
We solved this by scaling our API machines to **1GB of RAM**. It’s like giving our server a proper meal. While it adds a few dollars to the monthly cost, it ensures that the background seeding and Marten initialization have enough headroom to operate smoothly without gagging on memory limits.

```bash
fly scale memory 1024
```

### The "Double Underscore" Secret
Configuring secrets on Fly.io required a specific syntax. We learned the hard way that `CORS:AllowedOrigins` in `appsettings.json` must be set as `CORS__AllowedOrigins` (double underscore) in Fly secrets. It’s a small detail that can turn your deployment from a "success" into a "why isn't this working?!" frustration.

---

## Chapter 2: The Oracle Cloud Fortress

Oracle Cloud (OCI) offers an incredible "Always Free" tier, especially with their ARM-based Ampere A1 instances. But with great power comes great... firewall configuration. It’s like building a masterpiece inside a safe, only to realize you forgot the combination.

### The Issue: The Locked Gates
After deploying our containers to an OCI instance, we found that we couldn't access the Admin UI or the API from the outside world. The services were running perfectly in the background, but they were effectively trapped inside the VM.

### The Fix: IPTables & Security Lists
OCI images come with strict default `iptables` rules. Simply opening ports in the OCI Web Console wasn't enough; we had to manually unlock the doors on the VM itself. 

```bash
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

### Automation to the Rescue
To make this easier for everyone, we developed the `deploy-oracle.sh` script. It automates the entire setup—from installing Docker to configuring SSL with Caddy. Because life is too short to manually configure firewalls every time you want to launch a project.

---

## Key Learnings for Developers

1.  **Resource Profiling**: Don't assume default cloud limits are enough for startup-heavy applications. Know your app's "hunger" for memory.
2.  **Network Awareness**: Every cloud provider handles firewalls differently. OCI's dual-layer security is a common pitfall that can leave you scratching your head for hours.
3.  **CORS Consistency**: Always verify that your frontend URL matches the `AllowedOrigins` on your backend exactly. Even a missing trailing slash can break your heart (and your deployment).

***

### 🌿 Life Lesson from the Baryo
In the baryo, we know that the strongest house is only as good as the land it sits on. You can build the most beautiful structure, but if the ground is weak or the gates are locked, no one can enjoy it. Take the time to understand your "land"—your infrastructure. It might not be as glamorous as writing code, but it’s what keeps your dreams standing.

---
*Stay caffeinated,*

**Arnel Robles**  
Founder of [BaryoDev](https://github.com/arnelirobles)
