import assert from 'node:assert/strict';
import test from 'node:test';
import { isRepairsSettingsUpdated, isRepairsSettingsValid } from '../frontend/app/routes/settings/repairs/repairs-validation.ts';

test('PAR2 settings accept defaults and each supported fallback', () => {
    assert.equal(isRepairsSettingsValid({}), true);
    for (const fallback of ['arr-research', 'mark-only', 'delete']) {
        assert.equal(isRepairsSettingsValid({ 'repair.par2.fallback': fallback }), true);
    }
    assert.equal(isRepairsSettingsValid({ 'repair.par2.fallback': 'unknown' }), false);
});

test('storage budget must fit a nonnegative Int64', () => {
    for (const value of ['', '0', '1073741824', '9223372036854775807']) {
        assert.equal(isRepairsSettingsValid({ 'repair.par2.max-storage-bytes': value }), true, value);
    }
    for (const value of ['-1', '1.5', '1e6', 'invalid', '9223372036854775808']) {
        assert.equal(isRepairsSettingsValid({ 'repair.par2.max-storage-bytes': value }), false, value);
    }
});

test('concurrency must fit a positive Int32', () => {
    for (const value of ['', '1', '3', '2147483647']) {
        assert.equal(isRepairsSettingsValid({ 'repair.par2.max-concurrent': value }), true, value);
    }
    for (const value of ['0', '-1', '0.5', 'invalid', '2147483648']) {
        assert.equal(isRepairsSettingsValid({ 'repair.par2.max-concurrent': value }), false, value);
    }
});

test('each PAR2 field participates in unsaved change detection', () => {
    const config = {
        'repair.par2.enable': 'false',
        'repair.par2.fallback': 'arr-research',
        'repair.par2.max-storage-bytes': '0',
        'repair.par2.max-concurrent': '1',
    };
    assert.equal(isRepairsSettingsUpdated(config, { ...config }), false);
    for (const key of Object.keys(config)) {
        assert.equal(isRepairsSettingsUpdated(config, { ...config, [key]: 'changed' }), true, key);
    }
});
