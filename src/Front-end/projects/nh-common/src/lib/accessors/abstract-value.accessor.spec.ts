import {AbstractValueAccessor} from './abstract-value.accessor';

class TestValueAccessor extends AbstractValueAccessor {
}

describe('AbstractValueAccessor', () => {
  it('stores a written value without reporting it back', () => {
    const accessor = new TestValueAccessor();
    const reported: any[] = [];
    accessor.registerOnChange(value => reported.push(value));

    accessor.writeValue('NHP');

    expect(accessor.value).toBe('NHP');
    expect(reported).toEqual([]);
  });

  it('reports a changed value that is assigned to value', () => {
    const accessor = new TestValueAccessor();
    const reported: any[] = [];
    accessor.registerOnChange(value => reported.push(value));
    accessor.writeValue('NHP');

    accessor.value = 'NHP';
    accessor.value = 'OPS';

    expect(reported).toEqual(['OPS']);
  });
});
