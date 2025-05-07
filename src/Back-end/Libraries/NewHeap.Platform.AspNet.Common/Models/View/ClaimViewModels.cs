namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class NhClaimViewModel
{
    public string? Type { get; set; }
    public string? Value { get; set; }

    public class Property
    {
        public string? Key { get; set; }
        public string? Value { get; set; }
    }
}