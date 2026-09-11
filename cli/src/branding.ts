import { api } from './api.js';

export async function getBranding(server: string): Promise<{ productName: string, urls: { app: string } }> {
    try {
        const controllerPath = server.includes('localhost') ? '/api/meta/branding' : '/api/branding';
        // wait, let's just use /api/branding as the spec says
        return await api(server, '/api/branding');
    } catch (e) {
        console.error(`Could not reach ${server} — check the URL.`);
        process.exit(1);
    }
}
