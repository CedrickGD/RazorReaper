import { defaultWorkerTelemetryName } from '../constants';
import { isAuthorized } from '../lib/auth';
import { hashInstallId } from '../lib/hash';
import { jsonResponse } from '../lib/http';
import { validatePayload } from '../lib/validation';
import type { TelemetryEventName, TelemetryRequestBody, WorkerEnv } from '../types/telemetry';

export async function handleTelemetryEvent(request: Request, env: WorkerEnv): Promise<Response> {
	if (!isAuthorized(request, env.APP_SHARED_KEY)) {
		return jsonResponse(401, {
			error: 'unauthorized',
			message: 'Invalid X-App-Key.',
		});
	}

	if (!env.INSTALL_ID_PEPPER) {
		return jsonResponse(500, {
			error: 'server_misconfigured',
			message: 'INSTALL_ID_PEPPER secret is missing.',
		});
	}

	let body: TelemetryRequestBody;
	try {
		body = (await request.json()) as TelemetryRequestBody;
	} catch {
		return jsonResponse(400, {
			error: 'invalid_json',
			message: 'Request body must be valid JSON.',
		});
	}

	const validationError = validatePayload(body);
	if (validationError) {
		return jsonResponse(400, {
			error: 'invalid_payload',
			message: validationError,
		});
	}

	const installId = body.install_id!.trim().toLowerCase();
	const eventName = body.event_name!.trim() as TelemetryEventName;
	const appVersion = body.app_version!.trim();
	const platform = body.platform!.trim().toLowerCase();
	const eventUtc = new Date(body.timestamp_utc!.trim()).toISOString();
	const receivedUtc = new Date().toISOString();
	const propertiesJson = serializeProperties(body.properties);
	const installIdHash = await hashInstallId(installId, env.INSTALL_ID_PEPPER);

	await env.razorreaper_telemetry_prod
		.prepare(
			`INSERT INTO telemetry_events (
				install_id_hash,
				event_name,
				app_version,
				platform,
				event_utc,
				received_utc,
				properties_json
			) VALUES (?, ?, ?, ?, ?, ?, ?)`
		)
		.bind(installIdHash, eventName, appVersion, platform, eventUtc, receivedUtc, propertiesJson)
		.run();

	return jsonResponse(202, { accepted: true });
}

function serializeProperties(properties: TelemetryRequestBody['properties']): string | null {
	const normalized: Record<string, string> = {};

	if (properties && typeof properties === 'object') {
		for (const [key, value] of Object.entries(properties)) {
			const trimmedKey = key.trim();
			const trimmedValue = value.trim();
			if (trimmedKey.length === 0 || trimmedValue.length === 0) {
				continue;
			}

			normalized[trimmedKey] = trimmedValue;
		}
	}

	const workerIdentity = (
		normalized.worker_name ??
		normalized.workerName ??
		normalized.worker ??
		normalized.service ??
		''
	).trim();

	if (
		workerIdentity.length === 0 ||
		workerIdentity.toLowerCase() === 'unknown' ||
		workerIdentity === 'razorreaper-telemetry-backend'
	) {
		normalized.worker_name = defaultWorkerTelemetryName;
	} else {
		normalized.worker_name = workerIdentity;
	}

	return JSON.stringify(normalized);
}
