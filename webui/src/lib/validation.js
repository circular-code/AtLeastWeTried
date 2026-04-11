const playerApiKeyPattern = /^[0-9a-f]{64}$/i;
export function isValidPlayerApiKey(apiKey) {
    return playerApiKeyPattern.test(apiKey.trim());
}
export function isEditableTarget(target) {
    if (!(target instanceof HTMLElement)) {
        return false;
    }
    const tagName = target.tagName.toLowerCase();
    return target.isContentEditable || tagName === 'input' || tagName === 'textarea' || tagName === 'select';
}
export function numberValue(value, fallback) {
    return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}
export function stringValue(value, fallback) {
    return typeof value === 'string' && value.length > 0 ? value : fallback;
}
export function optionalStringValue(value) {
    return typeof value === 'string' && value.length > 0 ? value : undefined;
}
export function optionalNumberValue(value) {
    return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}
export function booleanValue(value, fallback) {
    return typeof value === 'boolean' ? value : fallback;
}
export function objectValue(value) {
    return typeof value === 'object' && value !== null ? value : undefined;
}
export function hasRecordKey(record, key) {
    return Object.prototype.hasOwnProperty.call(record, key);
}
