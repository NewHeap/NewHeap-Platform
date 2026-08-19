import {
  CollectionHttpRequestOptions,
  FilterRequestOptions
} from './http.models';

describe('CollectionHttpRequestOptions fluent API', () => {
  it('builds simple filters and ordering through shortcuts', () => {
    const options = new CollectionHttpRequestOptions()
      .equals('status', 'Active')
      .notEquals('archived', true)
      .isIn('ownerId', ['one', 'two'])
      .isNotIn('id', ['deleted'])
      .greaterThan('score', 0)
      .greaterThanOrEqual('createdAt', '2026-01-01')
      .lessThan('score', 100)
      .lessThanOrEqual('createdAt', '2026-12-31')
      .like('name', '%sample%')
      .orderAsc('name')
      .orderDesc('createdAt')
      .order('status', 'ASC')
      .orderByFirst('tasks', 'deadline', 'ASC')
      .orderByLast('tasks', 'deadline', 'DESC');

    expect(options.filter.map(filter => filter.operator)).toEqual([
      '==', '!=', 'IN', 'NOT IN', '>', '>=', '<', '<=', 'LIKE'
    ]);
    expect(options.orderBy).toEqual([
      jasmine.objectContaining({key: 'name', direction: 'ASC'}),
      jasmine.objectContaining({key: 'createdAt', direction: 'DESC'}),
      jasmine.objectContaining({key: 'status', direction: 'ASC'}),
      jasmine.objectContaining({key: 'tasks.{first:ASC}deadline', direction: 'ASC'}),
      jasmine.objectContaining({key: 'tasks.{last:DESC}deadline', direction: 'DESC'})
    ]);
  });

  it('handles an OR as the first or second fluent condition', () => {
    const first = FilterRequestOptions.equals('status', 'Active');
    const second = FilterRequestOptions.equals('status', 'Completed');
    const onlyOr = new CollectionHttpRequestOptions().or(first);
    const pairedOr = new CollectionHttpRequestOptions().equals('status', 'Active').or(second);

    expect(onlyOr.filter).toEqual([first]);
    expect(pairedOr.filter.length).toBe(1);
    expect(pairedOr.filter[0].ors).toEqual([second]);
  });

  it('preserves the existing serialized shape when OR follows multiple root filters', () => {
    const options = new CollectionHttpRequestOptions()
      .equals('status', 'Active')
      .equals('ownerId', 'owner-one')
      .or(FilterRequestOptions.equals('priority', 'High'));

    expect(options.filter.length).toBe(2);
    expect(options.filter[0].key).toBe('status');
    expect(options.filter[0].ands.map(filter => filter.key)).toEqual(['ownerId']);
    expect(options.filter[0].ors.map(filter => filter.key)).toEqual(['priority']);
    expect(options.filter[1].key).toBe('ownerId');
  });

  it('preserves the existing merge behavior for empty and falsy values', () => {
    const filters = [
      FilterRequestOptions.equals('enabled', false),
      FilterRequestOptions.equals('status', 0),
      FilterRequestOptions.equals('deadline', null),
      FilterRequestOptions.equals('empty-text', ''),
      FilterRequestOptions.in('empty-array', [])
    ];

    const mergedAnd = FilterRequestOptions.mergeToAndFilters(filters);
    const mergedOr = FilterRequestOptions.mergeToOrFilters(filters);

    expect(mergedAnd).toBeNull();
    expect(mergedOr).toBeNull();
  });

  it('composes condition arrays without exposing the payload arrays', () => {
    const root = FilterRequestOptions.equals('status', 'Active')
      .and(FilterRequestOptions.equals('ownerId', 'one'))
      .andArray([FilterRequestOptions.greaterThan('score', 0)])
      .or(FilterRequestOptions.equals('status', 'Completed'))
      .orArray([FilterRequestOptions.equals('priority', 'High')]);

    expect(root.ands.map(filter => filter.key)).toEqual(['ownerId', 'score']);
    expect(root.ors.map(filter => filter.key)).toEqual(['status', 'priority']);
  });
});
