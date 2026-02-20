import { jsonResponse } from './http';

export function isAuthorized(request: Request, appSharedKey?: string): boolean {
	if (!appSharedKey) {
		return false;
	}

	const incomingKey = request.headers.get('X-App-Key');
	if (!incomingKey) {
		return false;
	}

	return incomingKey.trim() === appSharedKey;
}

export function isAdminAuthorized(request: Request, adminApiKey?: string): boolean {
	if (!adminApiKey) {
		return false;
	}

	const incomingKey = request.headers.get('X-Admin-Key');
	if (!incomingKey) {
		return false;
	}

	return incomingKey.trim() === adminApiKey;
}

export function getAdminAuthError(request: Request, adminApiKey?: string): Response | null {
	if (!adminApiKey) {
		return jsonResponse(500, {
			error: 'server_misconfigured',
			message: 'ADMIN_API_KEY secret is missing.',
		});
	}

	if (!isAdminAuthorized(request, adminApiKey)) {
		return jsonResponse(401, {
			error: 'unauthorized',
			message: 'Invalid X-Admin-Key.',
		});
	}

	return null;
}

