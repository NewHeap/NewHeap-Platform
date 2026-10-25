namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The application's list of the people of the whole application (<see cref="NhAiSubjectRef.SystemType"/>):
/// everyone who works in it app-wide (<c>nh-system/global</c>), and everyone who works in one division
/// (<c>nh-system/division-{id}</c>). Register it with <c>UseSystemSubject(s => s.Members&lt;T&gt;())</c>.
/// <para>
/// The whole application keeps no member snapshot, because who passes its policies cannot be listed
/// without the application's user store. Notifications about agent work in its threads therefore consider
/// the people this list names next to the people of the thread, so a company-wide proposal in a thread
/// nobody opened still reaches the approvers. A list names candidates only: each person's role comes from
/// the read and approve policies of <c>UseSystemSubject</c> and the agent's view policy, evaluated for
/// them now as for a headless run, and in a division only for people who may work there. Listing a person
/// grants nothing, and the notification policy picks among them as for any subject.
/// </para>
/// </summary>
public interface INhAssistantSystemMembers
{
    /// <summary>
    /// The most people of one list a notification considers; the rest is not read.
    /// </summary>
    public const int MaxMembers = 200;

    /// <summary>
    /// The actor ids of the people of the whole application app-wide when <paramref name="divisionId"/>
    /// is <see langword="null"/>, otherwise of that division, in the order they should be considered, as
    /// the assistant's context resolver gives them for each person's own requests. Name people who
    /// should hear about decisions, such as the application's approvers, rather than every account; at
    /// most <see cref="MaxMembers"/> are read. Ids that are empty or longer than 256 characters are
    /// skipped.
    /// </summary>
    IAsyncEnumerable<string> ListAsync(Guid? divisionId, CancellationToken cancellationToken = default);
}
