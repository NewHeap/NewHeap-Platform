using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.Common.Models;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class ModelStateExtensionsTests
{
    [Fact]
    public void AdapterAppendsFieldAndGeneralErrorsWithoutMutatingTheResult()
    {
        var result = TaskResult<string>.Failed("title", "Title is required.");
        result.AddError("title", "Title must be unique.");
        result.WithKeylessError("The project cannot be saved.");
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("title", "Existing validation error.");

        result.ApplyToModelState(modelState);

        Assert.Equal(4, modelState.ErrorCount);
        Assert.Equal(["Existing validation error.", "Title is required.", "Title must be unique."],
            modelState["title"]!.Errors.Select(error => error.ErrorMessage));
        Assert.Equal("The project cannot be saved.", modelState[""]!.Errors.Single().ErrorMessage);
        Assert.False(result.Success);
        Assert.Equal(3, result.GetResultItems().Sum(item => item.ErrorMessages.Count));
    }

    [Fact]
    public void AdapterPreservesLegacyFormattingWhenALocalizerIsProvided()
    {
        FormattableString message = $"Project {"Alpha"} is unavailable.";
        var result = TaskResult.Failed("project", message);
        var localizer = Substitute.For<IStringLocalizer>();
        localizer[Arg.Any<string>(), Arg.Any<object[]>()]
            .Returns(new LocalizedString(message.Format, "Different localized message."));
        var modelState = new ModelStateDictionary();

        result.ApplyToModelState(modelState, localizer);

        Assert.Equal("Project Alpha is unavailable.", modelState["project"]!.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void BothConvenienceAdaptersPreserveErrorsAndSuccessfulResultsAddNothing()
    {
        var modelState = new ModelStateDictionary();
        TaskResult.Failed("General error.").ApplyTo(modelState);
        var returned = modelState.WithResultErrors(TaskResult<string>.Failed("name", "Field error."));
        TaskResult.Succeeded().ApplyToModelState(modelState);

        Assert.Same(modelState, returned);
        Assert.Equal(2, modelState.ErrorCount);
    }
}
