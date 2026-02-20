export function jsonResponse(status: number, data: unknown): Response {
	return new Response(JSON.stringify(data), {
		status,
		headers: corsHeaders(),
	});
}

export function corsHeaders(): HeadersInit {
	return {
		'content-type': 'application/json; charset=utf-8',
		'access-control-allow-origin': '*',
		'access-control-allow-methods': 'GET,POST,OPTIONS',
		'access-control-allow-headers': 'Content-Type, X-App-Key, X-Admin-Key',
	};
}

