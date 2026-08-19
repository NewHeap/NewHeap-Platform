import {
  enumIntValuesToArray,
  enumKeysToArray,
  enumStringValuesToArray,
  enumValuesToArray
} from './nh-common-util';

enum NumericStatus {
  Draft,
  Active
}

enum StringStatus {
  Draft = 'Draft',
  Active = 'Active'
}

describe('enum utilities', () => {
  it('preserves numeric enum reverse mappings for existing consumers', () => {
    expect(enumKeysToArray(NumericStatus)).toEqual(['0', '1', 'Draft', 'Active']);
    expect(enumValuesToArray(NumericStatus)).toEqual(['Draft', 'Active', 0, 1]);
    expect(enumIntValuesToArray(NumericStatus)).toEqual([0, 1]);
    expect(enumStringValuesToArray(NumericStatus)).toEqual(['Draft', 'Active']);
  });

  it('returns string enum keys and values', () => {
    expect(enumKeysToArray(StringStatus)).toEqual(['Draft', 'Active']);
    expect(enumValuesToArray(StringStatus)).toEqual(['Draft', 'Active']);
    expect(enumIntValuesToArray(StringStatus)).toEqual([]);
    expect(enumStringValuesToArray(StringStatus)).toEqual(['Draft', 'Active']);
  });
});
