import { allowedEventNames, defaultWorkerTelemetryName } from '../constants';
import { getAdminAuthError } from '../lib/auth';
import { addUtcDays, toUtcDateString, truncateToUtcDate } from '../lib/date';
import { jsonResponse } from '../lib/http';
import type { WorkerEnv } from '../types/telemetry';

export async function handleAdminOverview(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const result = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				(SELECT COUNT(*) FROM telemetry_events) AS total_events,
				(SELECT COUNT(DISTINCT install_id_hash) FROM telemetry_events) AS total_unique_installs,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE julianday(received_utc) >= julianday('now', '-1 day')) AS active_installs_24h,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE julianday(received_utc) >= julianday('now', '-7 day')) AS active_installs_7d,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE julianday(received_utc) >= julianday('now', '-30 day')) AS active_installs_30d,
				(SELECT MAX(received_utc) FROM telemetry_events) AS latest_received_utc`
		)
		.first<{
			total_events: number;
			total_unique_installs: number;
			active_installs_24h: number;
			active_installs_7d: number;
			active_installs_30d: number;
			latest_received_utc: string | null;
		}>();

	return jsonResponse(200, {
		total_events: result?.total_events ?? 0,
		total_unique_installs: result?.total_unique_installs ?? 0,
		active_installs_24h: result?.active_installs_24h ?? 0,
		active_installs_7d: result?.active_installs_7d ?? 0,
		active_installs_30d: result?.active_installs_30d ?? 0,
		latest_received_utc: result?.latest_received_utc ?? null,
	});
}

export async function handleAdminEventsByType(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const queryResult = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT event_name, COUNT(*) AS total
			 FROM telemetry_events
			 GROUP BY event_name
			 ORDER BY total DESC, event_name ASC`
		)
		.all<{ event_name: string; total: number }>();

	const totalsByName = new Map<string, number>();
	for (const row of queryResult.results) {
		totalsByName.set(row.event_name, row.total);
	}

	const items = Array.from(allowedEventNames).map((eventName) => ({
		event_name: eventName,
		total: totalsByName.get(eventName) ?? 0,
	}));

	return jsonResponse(200, { items });
}

export async function handleAdminDaily(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const url = new URL(request.url);
	const rawDays = url.searchParams.get('days');
	const parsedDays = rawDays ? Number.parseInt(rawDays, 10) : 30;
	const days = Number.isNaN(parsedDays) ? 30 : Math.min(Math.max(parsedDays, 1), 365);
	const dayOffset = `-${days - 1} day`;

	const queryResult = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				date(event_utc) AS day_utc,
				COUNT(*) AS total_events,
				COUNT(DISTINCT install_id_hash) AS unique_installs
			FROM telemetry_events
			WHERE date(event_utc) >= date('now', ?)
			GROUP BY day_utc
			ORDER BY day_utc ASC`
		)
		.bind(dayOffset)
		.all<{ day_utc: string; total_events: number; unique_installs: number }>();

	const rowsByDay = new Map<string, { total_events: number; unique_installs: number }>();
	for (const row of queryResult.results) {
		rowsByDay.set(row.day_utc, {
			total_events: row.total_events,
			unique_installs: row.unique_installs,
		});
	}

	const todayUtc = truncateToUtcDate(new Date());
	const startUtc = addUtcDays(todayUtc, -(days - 1));
	const items: Array<{ day_utc: string; total_events: number; unique_installs: number }> = [];

	for (let index = 0; index < days; index++) {
		const day = addUtcDays(startUtc, index);
		const key = toUtcDateString(day);
		const row = rowsByDay.get(key);
		items.push({
			day_utc: key,
			total_events: row?.total_events ?? 0,
			unique_installs: row?.unique_installs ?? 0,
		});
	}

	return jsonResponse(200, { days, items });
}

export async function handleAdminAppOpens(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const url = new URL(request.url);
	const rawDays = url.searchParams.get('days');
	const parsedDays = rawDays ? Number.parseInt(rawDays, 10) : 30;
	const days = Number.isNaN(parsedDays) ? 30 : Math.min(Math.max(parsedDays, 7), 365);
	const dayOffset = `-${days - 1} day`;

	const totals = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-1 day')) AS opens_24h,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-7 day')) AS opens_7d,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-30 day')) AS opens_30d,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start') AS opens_all_time,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-1 day')) AS unique_installs_24h,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-7 day')) AS unique_installs_7d,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-30 day')) AS unique_installs_30d,
				(SELECT COUNT(DISTINCT install_id_hash)
					FROM telemetry_events
					WHERE event_name = 'app_start') AS unique_installs_all_time,
				(SELECT MAX(received_utc)
					FROM telemetry_events
					WHERE event_name = 'app_start') AS latest_received_utc`
		)
		.first<{
			opens_24h: number;
			opens_7d: number;
			opens_30d: number;
			opens_all_time: number;
			unique_installs_24h: number;
			unique_installs_7d: number;
			unique_installs_30d: number;
			unique_installs_all_time: number;
			latest_received_utc: string | null;
		}>();

	const queryResult = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				date(event_utc) AS day_utc,
				COUNT(*) AS opens,
				COUNT(DISTINCT install_id_hash) AS unique_installs
			FROM telemetry_events
			WHERE event_name = 'app_start'
			AND date(event_utc) >= date('now', ?)
			GROUP BY day_utc
			ORDER BY day_utc ASC`
		)
		.bind(dayOffset)
		.all<{ day_utc: string; opens: number; unique_installs: number }>();

	const rowsByDay = new Map<string, { opens: number; unique_installs: number }>();
	for (const row of queryResult.results) {
		rowsByDay.set(row.day_utc, {
			opens: row.opens,
			unique_installs: row.unique_installs,
		});
	}

	const todayUtc = truncateToUtcDate(new Date());
	const startUtc = addUtcDays(todayUtc, -(days - 1));
	const items: Array<{ day_utc: string; opens: number; unique_installs: number }> = [];

	for (let index = 0; index < days; index++) {
		const day = addUtcDays(startUtc, index);
		const key = toUtcDateString(day);
		const row = rowsByDay.get(key);
		items.push({
			day_utc: key,
			opens: row?.opens ?? 0,
			unique_installs: row?.unique_installs ?? 0,
		});
	}

	return jsonResponse(200, {
		days,
		opens_24h: totals?.opens_24h ?? 0,
		opens_7d: totals?.opens_7d ?? 0,
		opens_30d: totals?.opens_30d ?? 0,
		opens_all_time: totals?.opens_all_time ?? 0,
		unique_installs_24h: totals?.unique_installs_24h ?? 0,
		unique_installs_7d: totals?.unique_installs_7d ?? 0,
		unique_installs_30d: totals?.unique_installs_30d ?? 0,
		unique_installs_all_time: totals?.unique_installs_all_time ?? 0,
		latest_received_utc: totals?.latest_received_utc ?? null,
		items,
	});
}

export async function handleAdminSessions(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const url = new URL(request.url);
	const rawDays = url.searchParams.get('days');
	const rawLimit = url.searchParams.get('limit');
	const parsedDays = rawDays ? Number.parseInt(rawDays, 10) : 7;
	const parsedLimit = rawLimit ? Number.parseInt(rawLimit, 10) : 25;
	const days = Number.isNaN(parsedDays) ? 7 : Math.min(Math.max(parsedDays, 1), 365);
	const limit = Number.isNaN(parsedLimit) ? 25 : Math.min(Math.max(parsedLimit, 1), 100);
	const dayOffset = `-${days - 1} day`;

	const summary = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-1 day')) AS sessions_started_24h,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-7 day')) AS sessions_started_7d,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start'
					AND julianday(received_utc) >= julianday('now', '-30 day')) AS sessions_started_30d,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_start') AS sessions_started_all_time,
				(SELECT COUNT(*)
					FROM telemetry_events
					WHERE event_name = 'app_session_end') AS sessions_ended_all_time,
				(SELECT COUNT(*)
					FROM (
						SELECT
							install_id_hash,
							COALESCE(
								NULLIF(
									CASE
										WHEN json_valid(properties_json) = 1
											THEN json_extract(properties_json, '$.session_id')
										ELSE NULL
									END,
									''
								),
								printf('legacy-%d', id)
							) AS session_id
						FROM telemetry_events
						WHERE event_name = 'app_start'
						AND julianday(received_utc) >= julianday('now', '-1 day')
					) starts
					LEFT JOIN (
						SELECT
							install_id_hash,
							COALESCE(
								NULLIF(
									CASE
										WHEN json_valid(properties_json) = 1
											THEN json_extract(properties_json, '$.session_id')
										ELSE NULL
									END,
									''
								),
								''
							) AS session_id
						FROM telemetry_events
						WHERE event_name = 'app_session_end'
					) ends
						ON starts.install_id_hash = ends.install_id_hash
						AND starts.session_id = ends.session_id
					WHERE ends.session_id IS NULL) AS active_sessions,
				(SELECT CAST(AVG(CASE
						WHEN json_valid(properties_json) = 1
							AND CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER) >= 0
						THEN CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER)
						ELSE NULL
					END) AS INTEGER)
					FROM telemetry_events
					WHERE event_name = 'app_session_end'
					AND julianday(received_utc) >= julianday('now', '-1 day')) AS avg_duration_seconds_24h,
				(SELECT CAST(AVG(CASE
						WHEN json_valid(properties_json) = 1
							AND CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER) >= 0
						THEN CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER)
						ELSE NULL
					END) AS INTEGER)
					FROM telemetry_events
					WHERE event_name = 'app_session_end'
					AND julianday(received_utc) >= julianday('now', '-7 day')) AS avg_duration_seconds_7d,
				(SELECT CAST(AVG(CASE
						WHEN json_valid(properties_json) = 1
							AND CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER) >= 0
						THEN CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER)
						ELSE NULL
					END) AS INTEGER)
					FROM telemetry_events
					WHERE event_name = 'app_session_end'
					AND julianday(received_utc) >= julianday('now', '-30 day')) AS avg_duration_seconds_30d,
				(SELECT CAST(AVG(CASE
						WHEN json_valid(properties_json) = 1
							AND CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER) >= 0
						THEN CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER)
						ELSE NULL
					END) AS INTEGER)
					FROM telemetry_events
					WHERE event_name = 'app_session_end') AS avg_duration_seconds_all_time,
				(SELECT MAX(received_utc)
					FROM telemetry_events
					WHERE event_name = 'app_start') AS latest_app_start_utc,
				(SELECT MAX(received_utc)
					FROM telemetry_events
					WHERE event_name = 'app_session_end') AS latest_session_end_utc`
		)
		.first<{
			sessions_started_24h: number;
			sessions_started_7d: number;
			sessions_started_30d: number;
			sessions_started_all_time: number;
			sessions_ended_all_time: number;
			active_sessions: number;
			avg_duration_seconds_24h: number | null;
			avg_duration_seconds_7d: number | null;
			avg_duration_seconds_30d: number | null;
			avg_duration_seconds_all_time: number | null;
			latest_app_start_utc: string | null;
			latest_session_end_utc: string | null;
		}>();

	const queryResult = await env.razorreaper_telemetry_prod
		.prepare(
			`WITH app_starts AS (
				SELECT
					install_id_hash,
					COALESCE(
						NULLIF(
							CASE
								WHEN json_valid(properties_json) = 1
									THEN json_extract(properties_json, '$.session_id')
								ELSE NULL
							END,
							''
						),
						printf('legacy-%d', id)
					) AS session_id,
					event_utc AS started_utc,
					received_utc AS started_received_utc
				FROM telemetry_events
				WHERE event_name = 'app_start'
				AND date(event_utc) >= date('now', ?)
			),
			app_ends AS (
				SELECT
					install_id_hash,
					COALESCE(
						NULLIF(
							CASE
								WHEN json_valid(properties_json) = 1
									THEN json_extract(properties_json, '$.session_id')
								ELSE NULL
							END,
							''
						),
						''
					) AS session_id,
					MAX(event_utc) AS ended_utc,
					MAX(received_utc) AS ended_received_utc,
					MAX(CASE
						WHEN json_valid(properties_json) = 1
							AND CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER) >= 0
							THEN CAST(json_extract(properties_json, '$.duration_seconds') AS INTEGER)
						ELSE NULL
					END) AS duration_seconds
				FROM telemetry_events
				WHERE event_name = 'app_session_end'
				GROUP BY install_id_hash, session_id
			)
			SELECT
				s.session_id,
				s.install_id_hash,
				s.started_utc,
				s.started_received_utc,
				e.ended_utc,
				e.ended_received_utc,
				CASE
					WHEN e.duration_seconds IS NOT NULL THEN e.duration_seconds
					WHEN e.ended_utc IS NOT NULL THEN MAX(CAST((julianday(e.ended_utc) - julianday(s.started_utc)) * 86400 AS INTEGER), 0)
					ELSE NULL
				END AS duration_seconds,
				CASE WHEN e.ended_utc IS NULL THEN 1 ELSE 0 END AS is_active
			FROM app_starts s
			LEFT JOIN app_ends e
				ON s.install_id_hash = e.install_id_hash
				AND s.session_id = e.session_id
			ORDER BY s.started_received_utc DESC
			LIMIT ?`
		)
		.bind(dayOffset, limit)
		.all<{
			session_id: string;
			install_id_hash: string;
			started_utc: string;
			started_received_utc: string;
			ended_utc: string | null;
			ended_received_utc: string | null;
			duration_seconds: number | null;
			is_active: number;
		}>();

	const items = queryResult.results.map((row) => ({
		session_id: row.session_id,
		install_id_hash: row.install_id_hash,
		started_utc: row.started_utc,
		started_received_utc: row.started_received_utc,
		ended_utc: row.ended_utc,
		ended_received_utc: row.ended_received_utc,
		duration_seconds: row.duration_seconds,
		is_active: row.is_active === 1,
	}));

	const sessionsStartedAllTime = summary?.sessions_started_all_time ?? 0;
	const sessionsEndedAllTime = summary?.sessions_ended_all_time ?? 0;
	const activeSessions = summary?.active_sessions ?? 0;

	return jsonResponse(200, {
		days,
		limit,
		latest_app_start_utc: summary?.latest_app_start_utc ?? null,
		latest_session_end_utc: summary?.latest_session_end_utc ?? null,
		active_sessions: activeSessions,
		sessions_started_24h: summary?.sessions_started_24h ?? 0,
		sessions_started_7d: summary?.sessions_started_7d ?? 0,
		sessions_started_30d: summary?.sessions_started_30d ?? 0,
		sessions_started_all_time: sessionsStartedAllTime,
		sessions_ended_all_time: sessionsEndedAllTime,
		avg_duration_seconds_24h: summary?.avg_duration_seconds_24h ?? null,
		avg_duration_seconds_7d: summary?.avg_duration_seconds_7d ?? null,
		avg_duration_seconds_30d: summary?.avg_duration_seconds_30d ?? null,
		avg_duration_seconds_all_time: summary?.avg_duration_seconds_all_time ?? null,
		items,
	});
}

export async function handleAdminWorkers(request: Request, env: WorkerEnv): Promise<Response> {
	const authError = getAdminAuthError(request, env.ADMIN_API_KEY);
	if (authError) {
		return authError;
	}

	const queryResult = await env.razorreaper_telemetry_prod
		.prepare(
			`SELECT
				COALESCE(
					NULLIF(json_extract(properties_json, '$.worker_name'), ''),
					NULLIF(json_extract(properties_json, '$.workerName'), ''),
					NULLIF(json_extract(properties_json, '$.worker'), ''),
					NULLIF(json_extract(properties_json, '$.service'), ''),
					'${defaultWorkerTelemetryName}'
				) AS worker_name,
				COUNT(*) AS total_events,
				COUNT(DISTINCT install_id_hash) AS unique_installs,
				MAX(received_utc) AS latest_received_utc
			FROM telemetry_events
			GROUP BY worker_name
			ORDER BY total_events DESC, worker_name ASC
			LIMIT 100`
		)
		.all<{
			worker_name: string;
			total_events: number;
			unique_installs: number;
			latest_received_utc: string | null;
		}>();

	const items = queryResult.results.map((row) => ({
		worker_name: row.worker_name,
		total_events: row.total_events,
		unique_installs: row.unique_installs,
		latest_received_utc: row.latest_received_utc,
	}));

	return jsonResponse(200, {
		items,
	});
}
