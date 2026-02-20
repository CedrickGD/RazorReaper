import { describe, expect, it } from 'vitest';
import { handleAdminDaily } from '../src/handlers/admin';
import type { WorkerEnv } from '../src/types/telemetry';

type PreparedStatementMock = {
	bind: (...args: unknown[]) => {
		all: <T>() => Promise<{ results: T[] }>;
	};
};

describe('handleAdminDaily', () => {
	it('queries full UTC calendar days for requested range', async () => {
		let capturedSql = '';
		let capturedBindArgs: unknown[] = [];

		const env = {
			ADMIN_API_KEY: 'secret',
			razorreaper_telemetry_prod: {
				prepare(sql: string): PreparedStatementMock {
					capturedSql = sql;
					return {
						bind(...args: unknown[]) {
							capturedBindArgs = args;
							return {
								all: async <T>() => ({ results: [] as T[] }),
							};
						},
					};
				},
			} as unknown as D1Database,
		} as WorkerEnv;

		const request = new Request('https://example.com/v1/admin/daily?days=30', {
			headers: {
				'X-Admin-Key': 'secret',
			},
		});

		const response = await handleAdminDaily(request, env);
		const payload = (await response.json()) as {
			days: number;
			items: Array<{ day_utc: string; total_events: number; unique_installs: number }>;
		};

		expect(response.status).toBe(200);
		expect(capturedSql).toContain("WHERE date(event_utc) >= date('now', ?)");
		expect(capturedBindArgs).toEqual(['-29 day']);
		expect(payload.days).toBe(30);
		expect(payload.items).toHaveLength(30);
	});
});
