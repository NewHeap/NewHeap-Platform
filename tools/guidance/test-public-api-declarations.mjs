import assert from 'node:assert/strict';
import { backEndDeclarations, frontEndDeclarations } from './public-api-declarations.mjs';

// A C# enum is recorded with its members, documentation and comments left out, so a new value is a change.
assert.deepEqual(backEndDeclarations(`namespace Sample;

/// <summary>Field types.</summary>
public enum FieldType
{
    String = 0,
    /// <summary>A date.</summary>
    Date = 7, // yyyy-MM-dd

    Time = 8
}
`), ['public enum FieldType { String = 0, Date = 7, Time = 8 }']);

// A base type on its own line, attributes on the enum and its members, and a one-line enum.
assert.deepEqual(backEndDeclarations(`[Flags]
public enum Access : byte
{
    None = 0,
    [Obsolete("Use Write")] Edit = 1,
    Write = 2,
}

public enum Short { A, B }
`), [
  '[Flags] public enum Access : byte { None = 0, [Obsolete("Use Write")] Edit = 1, Write = 2 }',
  'public enum Short { A, B }'
]);

// Other declarations keep ending at their first brace, and an enum nested in a class is read whole
// without taking the class's members after it.
assert.deepEqual(backEndDeclarations(`public sealed class Holder
{
    public enum Kind
    {
        First,
        Second
    }

    public Kind Value { get; init; }
}
`), [
  'public sealed class Holder {',
  'public enum Kind { First, Second }',
  'public Kind Value { get; init; }'
]);

// A TypeScript enum keeps a // inside a string value and drops a trailing comment.
assert.deepEqual(frontEndDeclarations(`export enum ClaimTypes {
  Name = 'http://schemas.example/name', // the name claim
  Role = 'role'
}
export function helper() {}
`), [
  "export enum ClaimTypes { Name = 'http://schemas.example/name', Role = 'role' }",
  'export function helper() {}'
]);

console.log('Verified the public API declarations, enum members included.');
