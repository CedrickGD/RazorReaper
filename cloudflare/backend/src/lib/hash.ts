export async function hashInstallId(installId: string, pepper: string): Promise<string> {
	const payload = new TextEncoder().encode(`${pepper}:${installId}`);
	const digest = await crypto.subtle.digest('SHA-256', payload);
	const bytes = new Uint8Array(digest);
	return Array.from(bytes)
		.map((value) => value.toString(16).padStart(2, '0'))
		.join('');
}

