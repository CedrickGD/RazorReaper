UPDATE telemetry_events
SET properties_json = CASE
	WHEN properties_json IS NULL OR TRIM(properties_json) = '' OR json_valid(properties_json) = 0
		THEN '{"worker_name":"razorreaper-app-telemetry"}'
	ELSE json_set(
		properties_json,
		'$.worker_name',
		CASE
			WHEN TRIM(
				COALESCE(
					json_extract(properties_json, '$.worker_name'),
					json_extract(properties_json, '$.workerName'),
					json_extract(properties_json, '$.worker'),
					json_extract(properties_json, '$.service'),
					''
				)
			) = ''
				THEN 'razorreaper-app-telemetry'
			WHEN LOWER(
				TRIM(
					COALESCE(
						json_extract(properties_json, '$.worker_name'),
						json_extract(properties_json, '$.workerName'),
						json_extract(properties_json, '$.worker'),
						json_extract(properties_json, '$.service'),
						''
					)
				)
			) = 'unknown'
				THEN 'razorreaper-app-telemetry'
			WHEN TRIM(
				COALESCE(
					json_extract(properties_json, '$.worker_name'),
					json_extract(properties_json, '$.workerName'),
					json_extract(properties_json, '$.worker'),
					json_extract(properties_json, '$.service'),
					''
				)
			) = 'razorreaper-telemetry-backend'
				THEN 'razorreaper-app-telemetry'
			ELSE TRIM(
				COALESCE(
					json_extract(properties_json, '$.worker_name'),
					json_extract(properties_json, '$.workerName'),
					json_extract(properties_json, '$.worker'),
					json_extract(properties_json, '$.service'),
					''
				)
			)
		END
	)
END
WHERE
	properties_json IS NULL
	OR TRIM(properties_json) = ''
	OR json_valid(properties_json) = 0
	OR TRIM(
		COALESCE(
			json_extract(properties_json, '$.worker_name'),
			json_extract(properties_json, '$.workerName'),
			json_extract(properties_json, '$.worker'),
			json_extract(properties_json, '$.service'),
			''
		)
	) = ''
	OR LOWER(
		TRIM(
			COALESCE(
				json_extract(properties_json, '$.worker_name'),
				json_extract(properties_json, '$.workerName'),
				json_extract(properties_json, '$.worker'),
				json_extract(properties_json, '$.service'),
				''
			)
		)
	) = 'unknown'
	OR TRIM(
		COALESCE(
			json_extract(properties_json, '$.worker_name'),
			json_extract(properties_json, '$.workerName'),
			json_extract(properties_json, '$.worker'),
			json_extract(properties_json, '$.service'),
			''
		)
	) = 'razorreaper-telemetry-backend';
