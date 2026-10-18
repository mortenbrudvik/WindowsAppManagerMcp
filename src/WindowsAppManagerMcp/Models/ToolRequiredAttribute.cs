namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A member of an argument object that the tool requires and never takes as null, although its type is nullable: it is
/// nullable only so that a missing member binds as null and the tool refuses it (InvalidArguments), for a batch item or a
/// step alone, instead of binding 0 or an enum's first value and acting on that. The input schema lists it as required and
/// without null (ToolJson), so the schema says what the tool takes; a client that checks its arguments against the schema
/// never sends the null the tool would refuse.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ToolRequiredAttribute : Attribute;
