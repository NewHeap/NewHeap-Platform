using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-400: client tools of the project assistant, which run in the person's browser, in the portal's own
/// interface, instead of on the server. The API declares them here, with their descriptions, inputs and
/// policies; the management portal registers a handler per tool id (<c>provideNhAssistantClientTools</c>)
/// and names them with each message, and the assistant offers them only in a person's own chat whose
/// browser can run them. <see cref="OpenProjectToolId"/> opens the project's page; <see cref="CreateTaskToolId"/>
/// hands a new task to the person: it opens the portal's create-task form prefilled, and the person saves
/// it through <c>POST project-tasks</c> with their own rights, so the assistant needs no proposal for it. A
/// run, a consultation or the messaging app's channel never gets them. Call it after
/// <c>AddSampleAssistant</c>, whose project assistant selects <see cref="ToolSetPrefix"/>.
/// </summary>
public static class SampleClientToolsComposition
{
    /// <summary>The prefix of the sample's client tools, which the project assistant selects.</summary>
    public const string ToolSetPrefix = "sample-ui";

    /// <summary>Opens the page of a project in the portal.</summary>
    public const string OpenProjectToolId = ToolSetPrefix + ".open-project";

    /// <summary>Opens the portal's create-task form of a project, prefilled.</summary>
    public const string CreateTaskToolId = ToolSetPrefix + ".create-task";

    /// <summary>The function names the model calls them by, as the sample's deterministic model does.</summary>
    public const string OpenProjectFunction = "sample_ui_open_project_v1";

    public const string CreateTaskFunction = "sample_ui_create_task_v1";

    /// <summary>The policy of creating a task, as <c>POST project-tasks</c> requires it.</summary>
    public const string CreateTaskPolicy = "app.project.manage";

    public static IServiceCollection AddSampleClientTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddNewHeapAssistant(assistant => assistant
            .AddClientTool<SampleOpenProjectInput>(OpenProjectToolId, tool => tool
                .Describe("Opens the page of one project in the person's portal, where they see its agent work and documents. Use it when the person asks to see or open a project whose id you know.")
                .RequirePolicy(SampleAssistantComposition.AccessPolicy))
            // A hand-off: the person reviews the prefilled form and saves it, so only someone who may create
            // tasks gets the tool, and only from an agent that may change data in a chat.
            .AddClientTool<SampleCreateTaskInput>(CreateTaskToolId, tool => tool
                .Describe("Opens the portal's form to create a task of one project, prefilled with a title.")
                .RequirePolicy(CreateTaskPolicy)
                .HandsOffChange())
            .WithLimits(limits => limits.ClientToolLifetime = TimeSpan.FromMinutes(15)));
        return services;
    }
}

/// <summary>The project the page opens.</summary>
public sealed record SampleOpenProjectInput(Guid ProjectId);

/// <summary>The project of the new task and the title the form starts with.</summary>
public sealed record SampleCreateTaskInput(Guid ProjectId, string Title);
