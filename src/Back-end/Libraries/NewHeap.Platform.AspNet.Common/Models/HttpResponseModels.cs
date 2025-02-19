using Microsoft.Extensions.Localization;

namespace NewHeap.Platform.AspNet.Common.Models;

public partial class BadRequestHttpResponseModel
{
    public BadRequestHttpResponseModel()
    {
        Errors = new List<string>();
    }

    public BadRequestHttpResponseModel(string error)
    {
        Errors = new List<string> { error };
    }

    public BadRequestHttpResponseModel(IEnumerable<string> errors)
    {
        Errors = errors;
    }

    public BadRequestHttpResponseModel(LocalizedString error)
    {
        Errors = new List<string> { error.Value };
    }

    public BadRequestHttpResponseModel(IEnumerable<LocalizedString> errors)
    {
        List<string>? errorList = new();

        foreach (var error in errors)
        {
            errorList.Add(error);
        }

        Errors = errorList;
    }

    public IEnumerable<string> Errors { get; set; }
}