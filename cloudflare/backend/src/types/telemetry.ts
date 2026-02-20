export type TelemetryEventName =
	| 'install_first_run'
	| 'app_start'
	| 'heartbeat'
	| 'update_check'
	| 'update_check_result'
	| 'navigation'
	| 'notification_shown'
	| 'app_error';

export type TelemetryRequestBody = {
	install_id?: string;
	event_name?: string;
	app_version?: string;
	timestamp_utc?: string;
	platform?: string;
	properties?: Record<string, string> | null;
};

export type WorkerEnv = Env & {
	razorreaper_telemetry_prod: D1Database;
	APP_SHARED_KEY?: string;
	INSTALL_ID_PEPPER?: string;
	ADMIN_API_KEY?: string;
};
