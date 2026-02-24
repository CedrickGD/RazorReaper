import { describe, expect, it } from 'vitest';
import { handleAdminSessions } from '../src/handlers/admin';
import type { WorkerEnv } from '../src/types/telemetry';

describe('handleAdminSessions', () => {
	it('returns summarized session metrics and recent session rows', async () => {
		let capturedBindArgs: unknown[] = [];

		const env = {
			ADMIN_API_KEY: 'secret',
			razorreaper_telemetry_prod: {
				prepare(sql: string) {
					if (sql.includes('sessions_started_24h')) {
						return {
							first: async <T>() =>
								({
									sessions_started_24h: 3,
									sessions_started_7d: 7,
									sessions_started_30d: 12,
									sessions_started_all_time: 20,
									sessions_ended_all_time: 15,
									active_sessions: 2,
									avg_duration_seconds_24h: 123,
									avg_duration_seconds_7d: 245,
									avg_duration_seconds_30d: 322,
									avg_duration_seconds_all_time: 412,
									latest_app_start_utc: '2026-02-24T08:00:00.000Z',
									latest_session_end_utc: '2026-02-24T08:10:00.000Z',
								}) as T,
						};
					}

					expect(sql).toContain('WITH app_starts AS');
					return {
						bind(...args: unknown[]) {
							capturedBindArgs = args;
							return {
								all: async <T>() => ({
									results: [
										{
											session_id: 'session-1',
											install_id_hash: 'install-hash-1',
											started_utc: '2026-02-24T08:00:00.000Z',
											started_received_utc: '2026-02-24T08:00:01.000Z',
											ended_utc: null,
											ended_received_utc: null,
											duration_seconds: null,
											is_active: 1,
										},
									] as T[],
								}),
							};
						},
					};
				},
			} as unknown as D1Database,
		} as WorkerEnv;

		const request = new Request('https://example.com/v1/admin/sessions?days=7&limit=2', {
			headers: {
				'X-Admin-Key': 'secret',
			},
		});

		const response = await handleAdminSessions(request, env);
		const payload = (await response.json()) as {
			days: number;
			limit: number;
			active_sessions: number;
			latest_app_start_utc: string | null;
			avg_duration_seconds_24h: number | null;
			items: Array<{
				session_id: string;
				is_active: boolean;
			}>;
		};

		expect(response.status).toBe(200);
		expect(capturedBindArgs).toEqual(['-6 day', 2]);
		expect(payload.days).toBe(7);
		expect(payload.limit).toBe(2);
		expect(payload.active_sessions).toBe(2);
		expect(payload.latest_app_start_utc).toBe('2026-02-24T08:00:00.000Z');
		expect(payload.avg_duration_seconds_24h).toBe(123);
		expect(payload.items).toHaveLength(1);
		expect(payload.items[0]).toMatchObject({
			session_id: 'session-1',
			is_active: true,
		});
	});
});
