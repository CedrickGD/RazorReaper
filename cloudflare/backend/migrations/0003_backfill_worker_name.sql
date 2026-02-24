UPDATE telemetry_events
SET properties_json = CASE
	WHEN properties_json IS NULL OR TRIM(properties_json) = '' OR json_valid(properties_json) = 0
		THEN '{"worker_name":"razorreaper-telemetry-backend"}'
	ELSE json_set(properties_json, '$.worker_name', 'razorreaper-telemetry-backend')
END
WHERE
	properties_json IS NULL
	OR TRIM(properties_json) = ''
	OR json_valid(properties_json) = 0
	OR (
		json_extract(properties_json, '$.worker_name') IS NULL
		AND json_extract(properties_json, '$.workerName') IS NULL
		AND json_extract(properties_json, '$.worker') IS NULL
		AND json_extract(properties_json, '$.service') IS NULL
	);
