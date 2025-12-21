import { test, expect } from '@playwright/test';

test.describe('Backups Operations', () => {
    test.beforeEach(async ({ page }) => {
        // Mock auth
        await page.addInitScript(() => {
            window.localStorage.setItem('barako_token', 'mock-token');
        });
    });

    test('should load backups list', async ({ page }) => {
        // Mock backups API
        await page.route('**/api/backups', async route => {
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    backups: [
                        { id: 'backup-1', name: 'backup-2023-01-01.zip', createdAt: '2023-01-01T00:00:00Z', size: '10MB', type: 'Full' }
                    ]
                }),
            });
        });

        await page.goto('/ops/backups');
        // Increase timeout for initial load/render
        await expect(page.getByText('Backups & Recovery')).toBeVisible({ timeout: 10000 });
        await expect(page.getByText('backup-2023-01-01.zip')).toBeVisible();
    });
});
