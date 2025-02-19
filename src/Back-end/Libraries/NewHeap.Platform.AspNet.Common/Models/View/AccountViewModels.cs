namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class ProfileAccountViewModel
{
    public ProfileAccountViewModel()
    {
        Claims = new List<ClaimViewModel>();
    }

    public UserViewModel User { get; set; }

    public List<DivisionViewModel> Divisions { get; set; } = new();

    public ICollection<ClaimViewModel> Claims { get; set; }
}