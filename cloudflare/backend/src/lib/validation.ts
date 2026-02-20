import {
	allowedEventNames,
	maxPropertiesCount,
	maxPropertyNameLength,
	maxPropertyValueLength,
	uuidRegex,
} from '../constants';
import type { TelemetryEventName, TelemetryRequestBody } from '../types/telemetry';

export function validatePayload(body: TelemetryRequestBody): string | null {
	if (!body.install_id || !uuidRegex.test(body.install_id.trim())) {
		return 'install_id must be a valid UUID.';
	}

	if (!body.event_name || !allowedEventNames.has(body.event_name.trim() as TelemetryEventName)) {
		return 'event_name is invalid.';
	}

	if (!body.app_version || body.app_version.trim().length === 0 || body.app_version.trim().length > 32) {
		return 'app_version is required and must be 1-32 chars.';
	}

	if (!body.platform || body.platform.trim().length === 0 || body.platform.trim().length > 32) {
		return 'platform is required and must be 1-32 chars.';
	}

	if (!body.timestamp_utc) {
		return 'timestamp_utc is required.';
	}

	const parsedDate = new Date(body.timestamp_utc);
	if (Number.isNaN(parsedDate.getTime())) {
		return 'timestamp_utc must be an ISO-8601 date.';
	}

	if (body.properties == null) {
		return null;
	}

	if (typeof body.properties !== 'object' || Array.isArray(body.properties)) {
		return 'properties must be an object when provided.';
	}

	const keys = Object.keys(body.properties);
	if (keys.length > maxPropertiesCount) {
		return `properties cannot contain more than ${maxPropertiesCount} keys.`;
	}

	for (const key of keys) {
		const trimmedKey = key.trim();
		if (trimmedKey.length === 0 || trimmedKey.length > maxPropertyNameLength) {
			return `property names must be 1-${maxPropertyNameLength} chars.`;
		}

		const value = body.properties[key];
		if (typeof value !== 'string') {
			return 'property values must be strings.';
		}

		const trimmedValue = value.trim();
		if (trimmedValue.length === 0 || trimmedValue.length > maxPropertyValueLength) {
			return `property values must be 1-${maxPropertyValueLength} chars.`;
		}
	}

	return null;
}
