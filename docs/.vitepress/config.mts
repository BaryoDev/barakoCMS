import { defineConfig } from 'vitepress'

export default defineConfig({
    title: "BarakoCMS",
    description: "The AI-Native, High-Performance Headless CMS for .NET 8",
    base: '/barakoCMS/',
    themeConfig: {
        logo: '/logo.webp',
        nav: [
            { text: 'Home', link: '/' },
            { text: 'Guide', link: '/guide/getting-started' },
            { text: 'Reference', link: '/reference/api' },
            { text: 'Workflows', link: '/workflows/index' }
        ],

        sidebar: [
            {
                text: 'Guide',
                items: [
                    { text: 'Introduction', link: '/guide/introduction' },
                    { text: 'Getting Started', link: '/guide/getting-started' },
                    { text: 'Configuration', link: '/guide/configuration' },
                    { text: 'Troubleshooting', link: '/guide/troubleshooting' }
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
                    { text: 'RBAC System', link: '/guide/rbac' },
                    { text: 'Workflow Engine', link: '/workflows/index' },
                    { text: 'Event Sourcing', link: '/core-concepts/event-sourcing' }, // New
                    { text: 'Optimistic Concurrency', link: '/core-concepts/concurrency' } // New
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
