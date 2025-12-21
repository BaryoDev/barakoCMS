# The Road to the Cloud: Our Journey with Fly.io and Oracle Cloud

Deploying a high-performance CMS like **BarakoCMS** isn't just about pushing code; it's about understanding the infrastructure that powers it. Over the past few weeks, we've taken BarakoCMS to the skies with **Fly.io** and built a fortress on **Oracle Cloud**. 

Here’s the story of the hurdles we faced, how we cleared them, and what we learned along the way.

---

## Chapter 1: The Fly.io Flight (and the OOM Turbulence)

Fly.io is fantastic for developers who want to deploy globally with ease. However, our initial "flight" was a bit bumpy.

### The Issue: "OOM Killed"
When we first ran `fly deploy`, our API server would crash almost immediately during the startup phase. The logs were clear: `Out of Memory (OOM)`. BarakoCMS uses **Marten** for event sourcing and document storage, and during the initial setup, it performs a series of data seeding tasks that are memory-intensive.

The default Fly.io machine (256MB or 512MB) simply wasn't enough to handle the initial surge.

### The Fix: Strategic Scaling
We solved this by scaling our API machines to **1GB of RAM**. While it adds a bit to the monthly cost, it ensures that the background seeding and Marten initialization have enough headroom to operate smoothly.

```bash
fly scale memory 1024
```

### The "Double Underscore" Secret
Configuring environment variables on Fly.io required a specific syntax to map to our nested .NET configuration. We learned that `CORS:AllowedOrigins` in `appsettings.json` must be set as `CORS__AllowedOrigins` (double underscore) in Fly secrets.

---

## Chapter 2: The Oracle Cloud Fortress

Oracle Cloud (OCI) offers an incredible "Always Free" tier, especially with their ARM-based Ampere A1 instances. But with great power comes great... firewall configuration.

### The Issue: The Locked Gates
After deploying our containers to an OCI instance, we found that we couldn't access the Admin UI or the API from the outside world, even though the services were running perfectly.

### The Fix: IPTables & Security Lists
Oracle Linux and Ubuntu images on OCI come with strict default `iptables` rules. Simply opening ports in the OCI Web Console (Security Lists) wasn't enough; we had to manually unlock the doors on the VM itself.

```bash
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

### Automation to the Rescue
To make this easier for everyone, we developed the `deploy-oracle.sh` script. It automates the entire setup—from installing Docker and Caddy to configuring SSL and generating secure credentials.

---

## Key Learnings for Developers

1.  **Resource Profiling**: Don't assume default cloud limits are enough for startup-heavy applications. Profile your app's memory usage during the initialization phase.
2.  **Network Awareness**: Every cloud provider handles firewalls differently. OCI's dual-layer security (Cloud-level and Instance-level) is a common pitfall.
3.  **CORS Consistency**: Always verify that your frontend URL matches the `AllowedOrigins` on your backend exactly. Even a missing trailing slash can break your deployment.

## Ready to Deploy?

Check out our official documentation for step-by-step instructions:

- 🚀 [Fly.io Deployment Guide](../guide/fly-io-deployment.md)
- ☁️ [Oracle Cloud Deployment Guide](../guide/oracle-cloud-deployment.md)
- 🛠️ [Troubleshooting Common Issues](../guide/troubleshooting.md)

Happy coding, and see you in the cloud!
