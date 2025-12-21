import { defineConfig } from 'vitepress'

// Version management
const VERSION = '2.1.0'

// https://vitepress.dev/reference/site-config
export default defineConfig({
    title: `BarakoCMS v${VERSION} `,
    description: 'The AI-Native, High-Performance Headless CMS for .NET 8',
    base: '/barakoCMS/',
    themeConfig: {
        logo: '/logo.webp',
        // https://vitepress.dev/reference/default-theme-config
        nav: [
            { text: 'Home', link: '/' },
            { text: 'Guide', link: '/guide/getting-started' },
            { text: `v${VERSION} `, link: '/versions' },
            { text: 'Reference', link: '/api/endpoints' },
            { text: 'Workflows', link: '/workflows/index' }
        ],

        sidebar: [
            {
                text: 'Guide',
                items: [
                    { text: 'Introduction', link: '/guide/introduction' },
                    { text: 'Getting Started', link: '/guide/getting-started' },
                    { text: 'Configuration', link: '/guide/configuration' },
                    { text: 'Admin UI', link: '/guide/admin-ui' },
                    { text: 'Observability & Logging', link: '/guide/observability' },
                    { text: 'Database Automation', link: '/guide/database-automation' },
                    { text: 'Kubernetes Deployment', link: '/guide/kubernetes-deployment' },
                    { text: 'Local Deployment', link: '/guide/local-deployment' },
                    { text: 'Fly.io Deployment', link: '/guide/fly-io-deployment' },
                    { text: 'Oracle Cloud Deployment', link: '/guide/oracle-cloud-deployment' },
                    { text: 'Troubleshooting', link: '/guide/troubleshooting' }
                ]
            },
            {
                text: 'Operations',
                items: [
                    { text: 'Backup & Recovery', link: '/guide/backup-recovery' }
                ]
            },
            {
                text: 'Tutorials',
                items: [
                    { text: 'Your First Content Type', link: '/tutorials/first-content-type' } // New
                ]
            },
            {
                text: 'Core Concepts',
                items: [
                    { text: 'Architecture Deep Dive', link: '/guide/architecture' },
                    { text: 'Content Modeling (Dynamic)', link: '/guide/content-modeling' }, // New
                    { text: 'RBAC System', link: '/guide/rbac' },
                    { text: 'Workflow Engine', link: '/workflows/index' },
                    { text: 'Workflow Plugins', link: '/workflows/plugins' },
                    { text: 'Plugin Examples', link: '/workflows/plugin-examples' },
                    { text: 'Event Sourcing', link: '/core-concepts/event-sourcing' },
                    { text: 'Optimistic Concurrency', link: '/core-concepts/concurrency' }
                ]
            },
            {
                text: 'Plugin Development',
                items: [
                    { text: 'Plugin Development Guide', link: '/plugin-development-guide' },
                    { text: 'Migration Guide', link: '/workflow-migration-guide' }
                ]
            },
            {
                text: 'Project',
                items: [
                    { text: 'Roadmap', link: '/roadmap' },
                    { text: 'Changelog', link: 'https://github.com/BaryoDev/barakoCMS/blob/master/CHANGELOG.md' }
                ]
            },
            {
                text: 'Reference',
                items: [
                    { text: 'API Endpoints', link: '/api/endpoints' },
                    { text: 'Error Codes', link: '/reference/error-codes' }
                ]
            }
        ],

        socialLinks: [
            { icon: 'github', link: 'https://github.com/baryodev/barakoCMS' }
        ],

        footer: {
            message: 'Released under the Apache 2.0 License.',
            copyright: 'Copyright © 2024-present Arnel Robles'
        }
    }
})
