namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class ProfileAccountViewModel
{
    public ProfileAccountViewModel()
    {
        Claims = new List<ClaimViewModel>();
    }

    public NhUserViewModel User { get; set; } = null!;

    public List<DivisionViewModel> Divisions { get; set; } = new();

    public ICollection<ClaimViewModel> Claims { get; set; }
}