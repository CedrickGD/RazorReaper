import type { TelemetryEventName } from './types/telemetry';

export const allowedEventNames = new Set<TelemetryEventName>([
	'install_first_run',
	'app_start',
	'app_session_end',
	'heartbeat',
	'update_check',
	'update_check_result',
	'navigation',
	'notification_shown',
	'app_error',
]);

export const uuidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
export const maxPropertiesCount = 16;
export const maxPropertyNameLength = 48;
export const maxPropertyValueLength = 160;
