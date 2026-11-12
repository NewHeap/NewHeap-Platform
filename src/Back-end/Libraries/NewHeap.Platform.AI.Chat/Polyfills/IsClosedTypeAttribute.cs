#if !NET11_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the C# 15 compiler writes on a <c>closed</c> type. .NET 11 declares it; the net10.0 build
/// declares it here, internally, so the internal closed hierarchies (<c>NhAssistantTurnEvent</c>,
/// <c>NhAssistantPartView</c>) compile on both targets. A closed type is checked by the compiler only:
/// its derived types live in this assembly, and a switch over it is exhaustive without a default arm.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class IsClosedTypeAttribute : Attribute
{
}
#endif
