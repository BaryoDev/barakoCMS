import { defineConfig } from 'vitepress'

export default defineConfig({
    title: "BarakoCMS",
    description: "The AI-Native, High-Performance Headless CMS for .NET 8",
    themeConfig: {
        nav: [
            { text: 'Home', link: '/' },
            { text: 'Guide', link: '/guide/getting-started' },
            { text: 'API', link: '/api/endpoints' },
            { text: 'Workflows', link: '/workflows/index' }
        ],

        sidebar: [
            {
                text: 'Introduction',
                items: [
                    { text: 'What is BarakoCMS?', link: '/guide/introduction' },
                    { text: 'Getting Started', link: '/guide/getting-started' }
                ]
            },
            {
                text: 'Core Concepts',
                items: [
                    { text: 'Architecture', link: '/guide/architecture' },
                    { text: 'RBAC System', link: '/guide/rbac' }
                ]
            },
            {
                text: 'Advanced',
                items: [
                    { text: 'Workflows', link: '/workflows/index' },
                    { text: 'API Reference', link: '/api/endpoints' }
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
