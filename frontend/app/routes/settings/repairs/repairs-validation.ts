export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["media.library-dir"] !== newConfig["media.library-dir"]
        || ["repair.par2.enable", "repair.par2.fallback", "repair.par2.max-storage-bytes", "repair.par2.max-concurrent"]
            .some(key => config[key] !== newConfig[key]);
}

function isIntegerInRange(value: string | undefined, min: bigint, max: bigint) {
    if (value === undefined || value === "") return true;
    return /^\d+$/.test(value) && BigInt(value) >= min && BigInt(value) <= max;
}

export function isStorageValid(value: string | undefined) {
    return isIntegerInRange(value, 0n, 9223372036854775807n);
}

export function isConcurrencyValid(value: string | undefined) {
    return isIntegerInRange(value, 1n, 2147483647n);
}

export function isRepairsSettingsValid(config: Record<string, string>) {
    return isStorageValid(config["repair.par2.max-storage-bytes"])
        && isConcurrencyValid(config["repair.par2.max-concurrent"])
        && [undefined, "arr-research", "mark-only", "delete"].includes(config["repair.par2.fallback"]);
}
