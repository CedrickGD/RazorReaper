import { env, createExecutionContext, waitOnExecutionContext, SELF } from 'cloudflare:test';
import { describe, it, expect } from 'vitest';
import worker from '../src/index';

// For now, you'll need to do something like this to get a correctly-typed
// `Request` to pass to `worker.fetch()`.
const IncomingRequest = Request<unknown, IncomingRequestCfProperties>;

describe('Telemetry worker', () => {
	it('responds with health payload (unit style)', async () => {
		const request = new IncomingRequest('http://example.com/health');
		// Create an empty context to pass to `worker.fetch()`.
		const ctx = createExecutionContext();
		const response = await worker.fetch(request, env, ctx);
		// Wait for all `Promise`s passed to `ctx.waitUntil()` to settle before running test assertions
		await waitOnExecutionContext(ctx);
		expect(response.status).toBe(200);
		expect(await response.json()).toMatchObject({
			ok: true,
			service: 'rr-telemetry-backend',
		});
	});

	it('rejects telemetry event without auth key (integration style)', async () => {
		const response = await SELF.fetch('https://example.com/v1/telemetry/event', {
			method: 'POST',
			headers: {
				'content-type': 'application/json',
			},
			body: JSON.stringify({
				install_id: '11111111-1111-4111-8111-111111111111',
				event_name: 'app_start',
				app_version: '1.3.8',
				timestamp_utc: '2026-02-19T00:00:00Z',
				platform: 'windows',
			}),
		});

		expect(response.status).toBe(401);
		expect(await response.json()).toMatchObject({
			error: 'unauthorized',
		});
	});

	it('rejects admin overview when admin secret is missing (integration style)', async () => {
		const response = await SELF.fetch('https://example.com/v1/admin/overview', {
			method: 'GET',
		});

		const payload = (await response.json()) as { error?: string };
		expect([401, 500]).toContain(response.status);
		expect(['server_misconfigured', 'unauthorized']).toContain(payload.error ?? '');
	});
});
