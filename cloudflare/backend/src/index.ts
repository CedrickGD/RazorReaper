import {
	handleAdminAppOpens,
	handleAdminDaily,
	handleAdminEventsByType,
	handleAdminOverview,
	handleAdminWorkers,
} from './handlers/admin';
import { handleTelemetryEvent } from './handlers/telemetry';
import { corsHeaders, jsonResponse } from './lib/http';
import type { WorkerEnv } from './types/telemetry';

export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const runtimeEnv = env as WorkerEnv;
		const url = new URL(request.url);

		if (request.method === 'OPTIONS') {
			return new Response(null, {
				status: 204,
				headers: corsHeaders(),
			});
		}

		if (request.method === 'GET' && url.pathname === '/health') {
			return jsonResponse(200, { ok: true, service: 'rr-telemetry-backend' });
		}

		if (request.method === 'POST' && url.pathname === '/v1/telemetry/event') {
			return handleTelemetryEvent(request, runtimeEnv);
		}

		if (request.method === 'GET' && url.pathname === '/v1/admin/overview') {
			return handleAdminOverview(request, runtimeEnv);
		}

		if (request.method === 'GET' && url.pathname === '/v1/admin/events-by-type') {
			return handleAdminEventsByType(request, runtimeEnv);
		}

		if (request.method === 'GET' && url.pathname === '/v1/admin/daily') {
			return handleAdminDaily(request, runtimeEnv);
		}

		if (request.method === 'GET' && url.pathname === '/v1/admin/app-opens') {
			return handleAdminAppOpens(request, runtimeEnv);
		}

		if (request.method === 'GET' && url.pathname === '/v1/admin/workers') {
			return handleAdminWorkers(request, runtimeEnv);
		}

		return jsonResponse(404, {
			error: 'not_found',
			message: 'Route not found.',
		});
	},
} satisfies ExportedHandler<Env>;

