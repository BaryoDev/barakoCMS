# Deploying to Oracle Cloud (Automated)

This guide covers how to deploy the full BarakoCMS stack (API, Admin, Database) to an **Oracle Cloud Infrastructure (OCI)** Compute Instance using our automated script.

## Prerequisites

1.  **Oracle Cloud Account** (Always Free Tier works).
2.  **VM Instance**: Ubuntu 22.04 or Oracle Linux 8 (Ampere A1 recommended for performance).
3.  **Domain Name**: A domain (e.g., `barakocms.com`) pointing to your VM's Public IP.
    -   Create `A` record: `api` -> `YOUR_VM_IP`
    -   Create `A` record: `admin` -> `YOUR_VM_IP`

## Step 1: Prepare the VM

SSH into your Oracle Cloud instance and update the system.

```bash
# Update system
sudo apt update && sudo apt upgrade -y

# Open Firewall Ports (HTTP/HTTPS)
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

## Step 2: Install Docker

If Docker is not installed, run this one-liner:

```bash
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
newgrp docker
```

## Step 3: Deployment

1.  **Clone the Repository**:
    ```bash
    git clone https://github.com/BaryoDev/barakoCMS.git
    cd barakoCMS
    ```

2.  **Run the Auto-Deploy Script**:
    ```bash
    chmod +x scripts/deploy-oracle.sh
    ./scripts/deploy-oracle.sh
    ```

3.  **Follow the Prompts**:
    -   Enter your base domain (e.g., `barakocms.com`).
    -   Enter your email (for SSL certificates).

The script will automatically:
-   Generate secure passwords for Database and Admin.
-   Create the `.env` configuration file.
-   Build the Admin UI (Next.js) from source.
-   Start all services (API, Admin, Postgres, Caddy).
-   Provision HTTPS certificates via Let's Encrypt.

## Step 4: Access Your CMS

Once the script finishes (it may take 5-10 minutes to build), your apps will be live:

-   **Admin Dashboard**: `https://admin.yourdomain.com`
-   **API Endpoint**: `https://api.yourdomain.com`

**⚠️ IMPORTANT**: Save the credentials output by the script! They are stored in `.env` if you need them later.

## Step 5: Automating Updates (CI/CD)

We have included a GitHub Actions workflow (`.github/workflows/deploy-oracle.yml`) to automatically deploy changes when you push to `master`.

### 1. Configure GitHub Secrets
Go to your GitHub Repository -> **Settings** -> **Secrets and variables** -> **Actions** -> **New repository secret**.

Add the following secrets:

| Secret Name      | Value                                                                                             |
| :--------------- | :------------------------------------------------------------------------------------------------ |
| `ORACLE_HOST`    | Your VM's Public IP Address (e.g., `123.45.67.89`)                                                |
| `ORACLE_USER`    | Your SSH Username (e.g., `ubuntu` or `opc`)                                                       |
| `ORACLE_SSH_KEY` | Your **Private SSH Key**. Copy the entire content of your `.pem` key (including `-----BEGIN...`). |

### 2. Push to Deploy
Once configured, any push to the `master` branch will trigger the workflow, pull the latest code on your server, and rebuild the containers.
