export function truncateToUtcDate(date: Date): Date {
	return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
}

export function addUtcDays(date: Date, days: number): Date {
	const clone = new Date(date);
	clone.setUTCDate(clone.getUTCDate() + days);
	return clone;
}

export function toUtcDateString(date: Date): string {
	return date.toISOString().slice(0, 10);
}

