namespace NewHeap.Platform.Common.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public partial class ClaimMatchOneAuthorizeAttribute : Attribute
{
    public ClaimMatchOneAuthorizeAttribute(string type, string value)
    {
        Type = type;
        Value = value;
    }

    public string Type { get; set; }
    public string Value { get; set; }
}