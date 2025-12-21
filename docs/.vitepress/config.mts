import { defineConfig } from 'vitepress'

export default defineConfig({
    title: "BarakoCMS",
    description: "The AI-Native, High-Performance Headless CMS for .NET 8",
    themeConfig: {
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
                    { text: 'Architecture Deep Dive', link: '/guide/architecture' },
                    { text: 'Configuration', link: '/guide/configuration' },
                    { text: 'Troubleshooting', link: '/guide/troubleshooting' }
                ]
            },
            {
                text: 'Core Concepts',
                items: [
                    { text: 'RBAC System', link: '/guide/rbac' },
                    { text: 'Workflow Engine', link: '/workflows/index' }
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
